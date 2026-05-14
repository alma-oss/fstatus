namespace Alma.Status

open System
open Microsoft.Extensions.Logging
open Alma.ServiceIdentification
open Alma.Status.Common

module StatusStorage =

    type PeriodicCheck = Async<unit>

    module private InternalState =
        open Alma.State.ConcurrentStorage

        let private internalState: State<SystemName * Instance, StatusItem> = State.empty()
        let private systems: State<SystemName, SoftwareSystem> = State.empty()

        let private initialServiceState (service: ServiceToCheck): StatusItem =
            Service {
                Name = service.Name
                Instance = service.Instance
                Status = Status.Warning (StatusMessage.create "Not checked yet")
                Tags =
                    service.Tags
                    |> List.map (function
                        | Tag tag -> Tag tag
                        | TagKV (key, value) -> TagKV (key, value)
                    )
            }

        let private initialDataObjectState (dataObject: DataObjectToCheck): StatusItem =
            DataObject {
                Name = dataObject.Name
                Status = Status.Warning (StatusMessage.create "Not checked yet")
                Details = dataObject.Details
                Tags =
                    dataObject.Tags
                    |> List.map (function
                        | Tag tag -> Tag tag
                        | TagKV (key, value) -> TagKV (key, value)
                    )
            }

        let setState systemName instance status =
            internalState
            |> State.set (Key (systemName, instance)) status

        let private setService systemName (service: ServiceToCheck) = setState systemName service.Instance
        let private setDataObject systemName (dataObject: DataObjectToCheck) = setState systemName dataObject.Instance

        let registerSystem (system: SoftwareSystem) =
            systems
            |> State.set (Key system.Name) system

            system.Services
            |> List.iter (fun service -> service |> initialServiceState |> setService system.Name service)

            system.DataObjects
            |> List.iter (fun dataObject -> dataObject |> initialDataObjectState |> setDataObject system.Name dataObject)

        let currentState () = internalState |> State.items |> List.map (fun (Key key, value) -> key, value) |> Map.ofList
        let all () = internalState |> State.values |> List.sortBy (fun status -> status.Name)
        let statusesForSystem systemName =
            internalState
            |> State.items
            |> List.filter (fun (Key (name, _), _) -> name = systemName)
            |> List.map snd

        let allSystems () = systems |> State.values

    [<RequireQualifiedAccess>]
    module private Changes =
        open System.Collections.Concurrent

        let queue: ConcurrentQueue<SystemName * StatusItem> = ConcurrentQueue()

    let private updateStatuses systemName statuses =
        let currentStatuses = InternalState.currentState()

        statuses
        |> List.choose (fun (instance, status) ->
            match currentStatuses.TryFind (systemName, instance), status with
            | Some currentStatus, _ when currentStatus.Status = status -> None

            | Some (System system), status -> Some (instance, System { system with Status = status })
            | Some (Service service), status -> Some (instance, Service { service with Status = status })
            | Some (DataObject dataObject), status -> Some (instance, DataObject { dataObject with Status = status })

            | _ -> None
        )
        |> List.iter (fun (instance, status) ->
            (systemName, status) |> Changes.queue.Enqueue
            InternalState.setState systemName instance status
        )

    [<TailCall>]
    let rec private periodicallyCheckServices onStatusChange (logger: ILogger) (system: SoftwareSystem): PeriodicCheck = async {
        logger.LogDebug("Checking services")

        match! system |> ServiceCheck.checkSystemServices onStatusChange logger with
        | Ok statuses -> updateStatuses system.Name statuses
        | Error errors ->
            errors
            |> List.iter (fun (error: string) -> logger.LogError("Checking {system} services ends with {error}", system.Name |> SystemName.value, error))

        logger.LogDebug("Wait for 15 seconds ...")
        do! Async.Sleep (TimeSpan.FromSeconds 15.)

        return! periodicallyCheckServices onStatusChange logger system
    }

    [<TailCall>]
    let rec private periodicallyCheckDataObjects onStatusChange (logger: ILogger) (system: SoftwareSystem): PeriodicCheck = async {
        logger.LogDebug("Checking data objects")

        match! system |> ServiceCheck.checkSystemDataObjects onStatusChange logger with
        | Ok statuses -> updateStatuses system.Name statuses
        | Error errors ->
            errors
            |> List.iter (fun (error: string) -> logger.LogError("Checking {system} resources ends with {error}", system.Name |> SystemName.value, error))

        logger.LogDebug("Wait for 1 hour ...")
        do! Async.Sleep (TimeSpan.FromHours 1)

        return! periodicallyCheckDataObjects onStatusChange logger system
    }

    let registerSystem onStatusChange (loggerFactory: ILoggerFactory) (system: SoftwareSystem): PeriodicCheck list =
        InternalState.registerSystem system
        let logger = loggerFactory.CreateLogger($"StatusStorage<{system.Name}>")

        logger.LogDebug("Checking resources")
        [
            system |> periodicallyCheckDataObjects onStatusChange (loggerFactory.CreateLogger($"CheckDataObjects<{system.Name}>"))
            system |> periodicallyCheckServices onStatusChange (loggerFactory.CreateLogger($"CheckServices<{system.Name}>"))
        ]

    let statuses (): StatusItem list = InternalState.all ()
    let systemStatuses (): StatusItem list =
        InternalState.allSystems ()
        |> List.map (fun system ->
            let statuses = InternalState.statusesForSystem system.Name

            System {
                Name = system.Name |> SystemName.value
                Status = statuses |> Status.foldItems
                Tags =
                    system.Tags
                    |> List.map (function
                        | Tag tag -> Tag tag
                        | TagKV (key, value) -> TagKV (key, value)
                    )
            }
        )

    let changes (): SystemStatusItem list =
        let changeSet =
            Changes.queue.ToArray()
            |> Array.toList
            |> List.rev
            |> List.distinctBy (fun (systemName, status) -> systemName, status.Name)

        Changes.queue.Clear()

        changeSet
        |> List.groupBy fst
        |> List.map (fun (systemName, changes) ->
            {
                Name = systemName |> SystemName.value
                Status = changes |> List.map snd |> Status.foldItems
                Tags = []
            }
        )
