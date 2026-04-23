namespace Alma.Status

open Microsoft.Extensions.Logging

module StreamLagSeverity =
    open Alma.Kafka

    type StreamLag = {
        Connection: ConnectionConfiguration
        GroupId: GroupId
        MaximumAllowedLag: int64
    }

    type GetCurrentLag = StreamLag -> int64

    type Dependencies = {
        LoggerFactory: ILoggerFactory
        /// When not provided, the current lag is calculated by querying Kafka. This is recommended for production.
        CurrentLag: GetCurrentLag option
    }

    [<RequireQualifiedAccess>]
    module StreamLag =
        let create brokerList stream maximumAllowedLag groupId = {
            Connection = { BrokerList = brokerList; Topic = stream }
            GroupId = groupId
            MaximumAllowedLag = maximumAllowedLag
        }

    let private currentLag (loggerFactory: ILoggerFactory) : GetCurrentLag =
        fun { Connection = connection; GroupId = groupId } ->
            let logger = loggerFactory.CreateLogger "StreamLagSeverity"

            logger.LogDebug (
                "Checking current lag for {groupId} in {stream}",
                groupId |> GroupId.value,
                connection.Topic |> StreamName.value
            )

            let lags = groupId |> Admin.lags logger connection |> Async.RunSynchronously

            let lag = lags |> List.sumBy Admin.PartitionLag.lag

            logger.LogDebug (
                "Current lag for {groupId} in {stream} is {lag} as sum from [{lags}]",
                groupId |> GroupId.value,
                connection.Topic |> StreamName.value,
                lag,
                lags
                |> List.map (fun { Admin.PartitionLag.Partition = partition; Lag = lag } ->
                    sprintf "%d:%d" partition lag)
                |> String.concat " | "
            )

            lag

    type private LagSeverity =
        | LagIsAllowedServiceCouldBeIdle
        | LagIsTooBigServiceShouldBeHealthy
        | ServiceIsHealthyButHasABigLag

    let private (|ConsumerIsUnhealthyButLagIsAllowed|_|)
        { LoggerFactory = loggerFactory; CurrentLag = getLag }
        streamLag
        =
        let currentLag = getLag |> Option.defaultValue (currentLag loggerFactory)

        function
        | Critical { Message = Some PublicErrorUnhealthy } ->
            let logger = loggerFactory.CreateLogger "StreamLagSeverity"

            if currentLag streamLag <= streamLag.MaximumAllowedLag then
                logger.LogDebug (
                    "Consumer ({groupId}) is unhealthy but stream ({stream}) has allowed lag.",
                    streamLag.GroupId |> GroupId.value,
                    streamLag.Connection.Topic |> StreamName.value
                )

                Some LagIsAllowedServiceCouldBeIdle
            else
                logger.LogDebug (
                    "Consumer ({groupId}) is unhealthy and stream ({stream}) has too big lag.",
                    streamLag.GroupId |> GroupId.value,
                    streamLag.Connection.Topic |> StreamName.value
                )

                Some LagIsTooBigServiceShouldBeHealthy
        | _ -> None

    let private (|ConsumerConsumesMoreStreamsAndIsAlreadyIdle|_|)
        { LoggerFactory = loggerFactory; CurrentLag = getLag }
        streamLag
        =
        let currentLag = getLag |> Option.defaultValue (currentLag loggerFactory)

        function
        | Info { Message = Some PublicInfoIdle } ->
            let logger = loggerFactory.CreateLogger "StreamLagSeverity"

            if currentLag streamLag <= streamLag.MaximumAllowedLag then
                logger.LogDebug (
                    "Consumer ({groupId}) consumes more streams ({stream}) and was already idle and has allowed lag in another stream.",
                    streamLag.GroupId |> GroupId.value,
                    streamLag.Connection.Topic |> StreamName.value
                )

                Some LagIsAllowedServiceCouldBeIdle
            else
                logger.LogDebug (
                    "Consumer ({groupId}) is idle but another stream ({stream}) has too big lag.",
                    streamLag.GroupId |> GroupId.value,
                    streamLag.Connection.Topic |> StreamName.value
                )

                Some LagIsTooBigServiceShouldBeHealthy
        | _ -> None

    let private (|ConsumerIsHealthyButHasABigLag|_|) { LoggerFactory = loggerFactory; CurrentLag = getLag } streamLag =
        let currentLag = getLag |> Option.defaultValue (currentLag loggerFactory)

        function
        | Success _ ->
            let logger = loggerFactory.CreateLogger "StreamLagSeverity"

            if currentLag streamLag > streamLag.MaximumAllowedLag then
                logger.LogDebug (
                    "Service ({groupId}) is healthy but has big lag in stream ({stream}).",
                    streamLag.GroupId |> GroupId.value,
                    streamLag.Connection.Topic |> StreamName.value
                )

                Some ServiceIsHealthyButHasABigLag
            else
                None
        | _ -> None

    let private toCheckResult streamLag = function
        | LagIsAllowedServiceCouldBeIdle ->
            Info {
                Message = Some PublicInfoIdle
                Note = Some (sprintf "%s; lag <= %d" PublicErrorUnhealthy streamLag.MaximumAllowedLag)
            }

        | LagIsTooBigServiceShouldBeHealthy ->
            Critical {
                Message = Some PublicErrorUnhealthyWithLag
                Note = Some (sprintf "%s; lag > %d" PublicErrorUnhealthy streamLag.MaximumAllowedLag)
            }

        | ServiceIsHealthyButHasABigLag ->
            Critical {
                Message = Some PublicErrorHealthyWithLag
                Note = Some (sprintf "Healthy; lag > %d" streamLag.MaximumAllowedLag)
            }

    let idleOnUnhealthyUpToLag dependencies streamLag : Severity =
        Check.map (function
            | ConsumerIsUnhealthyButLagIsAllowed dependencies streamLag lagSeverity
            | ConsumerConsumesMoreStreamsAndIsAlreadyIdle dependencies streamLag lagSeverity
            | ConsumerIsHealthyButHasABigLag dependencies streamLag lagSeverity ->
                lagSeverity |> toCheckResult streamLag

            | res -> res)
