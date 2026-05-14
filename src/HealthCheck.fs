namespace Alma.Status

open System
open Microsoft.Extensions.Logging
open Feather.ErrorHandling
open Alma.ServiceIdentification
open Alma.EnvironmentModel

module HealthCheck =
    open System.Net
    open Alma.Kafka
    open Alma.State
    open Alma.WebApplication
    open Alma.WebApplication.Http
    open Alma.WebApplication.OAuth

    type CreatePublicUrlWithPath = Tier -> Instance -> string -> Url

    let localUrlWithPath instance (path: string) =
        sprintf "%s/%s" (instance |> Instance.k8sLocalServiceUrl) (path.TrimStart ('/'))
        |> Url

    let private httpPublicError: HttpError -> string = function
        | HttpError.ResponseError { StatusCode = HttpStatusCode.Unauthorized } -> PublicErrorUnauthorized
        | HttpError.ResponseError { StatusCode = HttpStatusCode.Forbidden } -> PublicErrorForbidden
        | _ -> PublicErrorUnhealthy

    let handleHttpAsyncResult xA =
        xA |> AsyncResult.mapError httpPublicError |> AsyncResult.ignore

    let private recheckSeconds = 15

    let private checkAgain check error =
        async {
            do! Async.Sleep (TimeSpan.FromSeconds (float recheckSeconds))

            match! check with
            | Ok _ -> return CheckResult.warning error
            | Error error -> return CheckResult.critical error
        }

    let healthCheckOnPath (loggerFactory: ILoggerFactory) path headers (method: string) instance =
        let logger = loggerFactory.CreateLogger("HealthCheck")
        let url = localUrlWithPath instance path
        let check = async {
            logger.LogDebug("[{method}] {url}", method, url)

            return!
                match method.ToUpper() with
                | "HEAD" -> Http.head headers url |> handleHttpAsyncResult
                | "GET" -> Http.get headers url |> handleHttpAsyncResult
                | _ -> AsyncResult.ofError "Invalid check"
        }

        {
            Name = CheckName.ofInstance "HealthCheck" instance
            Execute = async {
                match! check with
                | Ok _ -> return CheckResult.success
                | Error error ->
                    logger.LogWarning("[{method}] {url} - Recheck in {recheckSeconds}s ...", method, url, recheckSeconds)
                    return! checkAgain check error
            }
        }

    let healthCheck (loggerFactory: ILoggerFactory) =
        healthCheckOnPath loggerFactory "/health-check"

    /// Is success when the service health check is forbidden without credentials.
    let healthCheckSecuredResourcePath (loggerFactory: ILoggerFactory) path (method: string) instance =
        let logger = loggerFactory.CreateLogger("SecuredHealthCheck")
        let url = localUrlWithPath instance path

        let assertIsSecured (response: AsyncResult<_, HttpError>): Async<CheckResult> = async {
            match! response with
            | Ok _ -> return CheckResult.critical "Insecure"
            | Error (HttpError.ResponseError { StatusCode = HttpStatusCode.Unauthorized })
            | Error (HttpError.ResponseError { StatusCode = HttpStatusCode.Forbidden }) -> return CheckResult.success
            | Error e -> return e |> httpPublicError |> CheckResult.critical
        }

        {
            Name = CheckName.ofInstance "SecuredHealthCheck" instance
            Execute = async {
                logger.LogDebug("[{method}] {url}", method, url)

                return!
                    match method.ToUpper() with
                    | "HEAD" -> Http.head [] url |> assertIsSecured
                    | "GET" -> Http.get [] url |> assertIsSecured
                    | _ -> Async.retn (CheckResult.critical "Invalid check")
            }
        }

    /// Is success when the service health check is forbidden without credentials.
    let healthCheckSecuredResource (loggerFactory: ILoggerFactory) (method: string) instance =
        healthCheckSecuredResourcePath loggerFactory "/health-check" method instance

    let healthCheckWithOAuthPath (createPublicUrlWithPath: CreatePublicUrlWithPath) (loggerFactory: ILoggerFactory) path currentTier region credentials (method: string) instance =
        let logger = loggerFactory.CreateLogger("HealthCheck")
        let url = createPublicUrlWithPath currentTier instance path
        let check = async {
            logger.LogDebug("[{method}] {url}", method, url)
            let! token = credentials |> requestTokenFromCognito region instance (TimeSpan.FromHours(20))

            match token with
            | Ok token ->
                let headers = [ token |> OAuthToken.asAuthorizationHeader ]

                return!
                    match method.ToUpper() with
                    | "HEAD" -> Http.head headers url |> handleHttpAsyncResult
                    | "GET" -> Http.get headers url |> handleHttpAsyncResult
                    | _ -> AsyncResult.ofError "Invalid check"

            | Error e ->
                logger.LogError("Unauthorized[{method}] {url}: {error}", method, url, e |> HttpError.format)
                return! AsyncResult.ofError "Unauthorized"
        }

        {
            Name = CheckName.ofInstance "HealthCheck" instance
            Execute = async {
                match! check with
                | Ok _ -> return CheckResult.success
                | Error error ->
                    logger.LogWarning("[{method}] {url} - Recheck in {recheckSeconds}s ...", method, url, recheckSeconds)
                    return! checkAgain check error
            }
        }

    let healthCheckWithOAuth (createPublicUrlWithPath: CreatePublicUrlWithPath) (loggerFactory: ILoggerFactory) currentTier region credentials (method: string) instance =
        healthCheckWithOAuthPath createPublicUrlWithPath loggerFactory "/health-check" currentTier region credentials method instance

    let private getAllStreams (loggerFactory: ILoggerFactory) kafkaBrokerList =
        let fetchTopics () = asyncResult {
            let logger = loggerFactory.CreateLogger("Kafka.Admin")
            logger.LogInformation("Fetching streams")

            try
                use admin = Admin.createAdmin kafkaBrokerList

                try
                    return admin |> Admin.getAllTopics
                with e ->
                    logger.LogError("Streams error: {error}", e)
                    return! AsyncResult.ofError e

            with e ->
                logger.LogError("Kafka error: {error}", e)
                return! AsyncResult.ofError e
        }

        let key = "kafka.streams"
        let ttl = (TimeSpan.FromMinutes(30.).TotalMilliseconds |> int) * 1<TemporaryCache.Millisecond>
        let fetchWithTimeout () = fetchTopics () |> Async.withTimeout 5000 (Ok [])

        TemporaryCache.load key fetchWithTimeout ttl

    let healthCheckForStream (loggerFactory: ILoggerFactory) kafkaBrokerList stream = {
        Name = CheckName.ofInstance "StreamHealthCheck" stream
        Execute = async {
            let logger = loggerFactory.CreateLogger("StreamHealthCheck")
            logger.LogDebug("Checking stream exists")

            match! getAllStreams loggerFactory kafkaBrokerList with
            | Ok streams when streams |> List.contains (Instance stream) -> return CheckResult.success
            | _ -> return CheckResult.critical "Stream does not exist"
        }
    }

    let k8sServiceAtPort (port: int) instance (path: string) =
        sprintf "%s:%d/%s"
            (instance |> Instance.k8sLocalServiceUrl)
            port
            (path.TrimStart('/'))
        |> Url

    /// Builds URLs targeting all pods of a StatefulSet.
    /// Useful for sentinel-backed Redis where the master pod may vary across environments.
    /// Pattern: http://{context}-{purpose}-{version}-{i}.{context}-{purpose}-{version}.{domain}.svc.cluster.local:{port}/{path}
    let k8sSTSPodsAtPort (port: int) (podCount: int) (instance: Instance) (path: string) : Url list =
        let (Domain domain) = instance.Domain
        let (Context context) = instance.Context
        let (Purpose purpose) = instance.Purpose
        let (Version version) = instance.Version
        let slug = sprintf "%s-%s-%s" (context.ToLower()) purpose version

        [ 0 .. podCount - 1 ]
        |> List.map (fun i ->
            sprintf "http://%s-%d.%s.%s.svc.cluster.local:%d/%s"
                slug i slug domain port (path.TrimStart('/'))
            |> Url
        )

    let parseMetricValue (metricName: string) (body: string): string option =
        body.Split('\n')
        |> Array.tryPick (fun line ->
            let trimmed = line.Trim()
            if not (trimmed.StartsWith "#") && (trimmed.StartsWith(metricName + " ") || trimmed.StartsWith(metricName + "{"))
            then
                let parts = trimmed.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                if parts.Length >= 2 then Some parts[1]
                else None
            else
                None
        )

    let private fetchMetricFloat (logger: ILogger) (metricName: string) (Url urlStr as url) : Async<float option> =
        async {
            match! Http.get [] url with
            | Error e ->
                logger.LogWarning("Metrics endpoint unavailable at {url}: {error}", urlStr, e |> HttpError.format)
                return None
            | Ok body ->
                match body |> parseMetricValue metricName with
                | None ->
                    logger.LogDebug("Metric {metricName} not found at {url}", metricName, urlStr)
                    return None
                | Some value ->
                    match Double.TryParse(value, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
                    | true, v -> return Some v
                    | false, _ ->
                        logger.LogWarning("Cannot parse metric value '{value}' at {url}", value, urlStr)
                        return None
        }

    let healthCheckMetricAboveZero (loggerFactory: ILoggerFactory) (metricName: string) (urls: Url list) instance =
        let logger = loggerFactory.CreateLogger($"MetricHealthCheck<{metricName}>")

        {
            Name = CheckName.ofInstance (sprintf "Metric(%s)" metricName) instance
            Execute = async {
                let! results =
                    urls
                    |> List.map (fetchMetricFloat logger metricName)
                    |> Async.Parallel
                let values = results |> Array.choose id
                if values.Length = 0 then
                    return CheckResult.critical (sprintf "Metric '%s' not available on any of %d pods" metricName urls.Length)
                else
                    let sum = values |> Array.sum
                    if sum > 0.0 then
                        return CheckResult.success
                    else
                        return CheckResult.critical (sprintf "%s sum = %g across %d pods (expected > 0)" metricName sum values.Length)
            }
        }
