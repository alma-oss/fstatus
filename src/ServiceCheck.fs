namespace Alma.Status

open Microsoft.Extensions.Logging
open Feather.ErrorHandling
open Alma.ServiceIdentification
open Alma.Status.Common

module internal ServiceCheck =
    let private executeCheck instance =
        Check.execute
        >> Async.map (fun checkResult -> instance, checkResult |> CheckResult.toStatus)

    let private executeComponentChecks resourceKind (onStatusChange: OnStatusChange) (instance: Instance) checks =
        checks
        |> List.map (executeCheck instance)
        |> Async.Sequential
        |> Async.map (fun statuses ->
            instance,
            statuses
            |> List.ofArray
            |> List.map snd
            |> Status.fold
        )
        |> Async.tee (fun (instance, status) ->
            onStatusChange {
                ResourceKind = resourceKind
                Instance = instance
                Status = status
            }
        )

    let checkSystemServices onStatusChange (logger: ILogger) (system: SoftwareSystem): AsyncResult<(Instance * Status) list, string list> = asyncResult {
        let! servicesStatuses =
            system.Services
            |> List.map (fun service ->
                logger.LogDebug("Checking service {service}", service.Name)

                service.Checks
                |> executeComponentChecks ServiceResource onStatusChange service.Instance
            )
            |> AsyncResult.ofParallelAsyncs (sprintf "%A")

        return servicesStatuses
    }

    let checkSystemDataObjects onStatusChange (logger: ILogger) (system: SoftwareSystem): AsyncResult<(Instance * Status) list, string list> = asyncResult {
        let! dataObjectStatuses =
            system.DataObjects
            |> List.map (fun dataObject ->
                logger.LogDebug("Checking dataObject {service}", dataObject.Name)

                dataObject.Checks
                |> executeComponentChecks DataObjectResource onStatusChange dataObject.Instance
            )
            |> AsyncResult.ofParallelAsyncs (sprintf "%A")

        return dataObjectStatuses
    }
