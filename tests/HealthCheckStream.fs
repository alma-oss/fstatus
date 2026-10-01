module Alma.Status.Tests.HealthCheckStream

open System
open Expecto
open Microsoft.Extensions.Logging.Abstractions
open Alma.Status
open Alma.Status.HealthCheck
open Alma.Kafka
open Alma.ServiceIdentification

let private loggerFactory =
    NullLoggerFactory.Instance :> Microsoft.Extensions.Logging.ILoggerFactory

let private stream =
    Instance.createFromValues
        (Domain "test")
        (Context "monitoring")
        (Purpose "kafka")
        (Version "v1")

let private runCheck brokers =
    let check = healthCheckForStream loggerFactory (BrokerList brokers) stream
    check.Execute |> Async.RunSynchronously

let private criticalMessage = function
    | Critical { Message = Some message } -> Some message
    | _ -> None

[<Tests>]
let streamHealthCheckTests =
    testList "healthCheckForStream" [
        testCase "unreachable broker reports streams unavailable, not a missing stream" <| fun _ ->
            let result = runCheck "127.0.0.1:1"

            match criticalMessage result with
            | Some message ->
                Expect.stringStarts message "Streams unavailable" "A broker failure must not read as a missing stream"
            | None ->
                failtest "Expected a critical result for an unreachable broker"

        testCase "repeated failure is not cached as an empty stream list" <| fun _ ->
            let first = runCheck "127.0.0.1:2" |> criticalMessage
            let second = runCheck "127.0.0.1:2" |> criticalMessage

            match second with
            | Some message ->
                Expect.stringStarts message "Streams unavailable" "A cached error must not degrade into 'Stream does not exist'"
            | None ->
                failtest "Expected a critical result on the second call"
    ]

[<Tests>]
let streamsErrorFormatTests =
    testList "StreamsError.format" [
        testCase "timeout renders seconds" <| fun _ ->
            let message = StreamsError.format (StreamsError.Timeout (TimeSpan.FromSeconds 5.))

            Expect.equal message "timeout after 5 s" "Timeout should render whole seconds"

        testCase "no streams names the brokers" <| fun _ ->
            let message = StreamsError.format (StreamsError.NoStreams (BrokerList "broker:9092"))

            Expect.equal message "no streams returned by broker:9092" "NoStreams should name the brokers"

        testCase "kafka error uses the exception message" <| fun _ ->
            let message = StreamsError.format (StreamsError.Kafka (exn "boom"))

            Expect.equal message "boom" "Kafka error should use the exception message"
    ]
