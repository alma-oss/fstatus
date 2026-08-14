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
        let private domainState: State<DomainName, StatusItem> = State.empty()

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

        let private initialDomainState (domain: DomainToCheck): StatusItem =
            Domain {
                Name = domain.Name |> DomainName.value
                Status = Status.Warning (StatusMessage.create "Not checked yet")
                Tags =
                    domain.Tags
                    |> List.map (function
                        | Tag tag -> Tag tag
                        | TagKV (key, value) -> TagKV (key, value)
                    )
            }

        let setSystemState systemName instance status =
            internalState
            |> State.set (Key (systemName, instance)) status

        let setDomainState name status =
            domainState
            |> State.set (Key name) status

        let private setService systemName (service: ServiceToCheck) = setSystemState systemName service.Instance
        let private setDataObject systemName (dataObject: DataObjectToCheck) = setSystemState systemName dataObject.Instance

        let registerSystem (system: SoftwareSystem) =
            systems
            |> State.set (Key system.Name) system

            system.Services
            |> List.iter (fun service -> service |> initialServiceState |> setService system.Name service)

            system.DataObjects
            |> List.iter (fun dataObject -> dataObject |> initialDataObjectState |> setDataObject system.Name dataObject)

        let registerDomain (domain: DomainToCheck) =
            domain |> initialDomainState |> setDomainState domain.Name

        let currentSystemState () = internalState |> State.items |> List.map (fun (Key key, value) -> key, value) |> Map.ofList
        let all () = internalState |> State.values |> List.sortBy (fun status -> status.Name)
        let statusesForSystem systemName =
            internalState
            |> State.items
            |> List.filter (fun (Key (name, _), _) -> name = systemName)
            |> List.map snd

        let allSystems () = systems |> State.values

        let currentDomainState () = domainState |> State.items |> List.map (fun (Key key, value) -> key, value) |> Map.ofList
        let allDomains () = domainState |> State.values |> List.sortBy (fun status -> status.Name)

    [<RequireQualifiedAccess>]
    module private Changes =
        open System.Collections.Concurrent

        let queue: ConcurrentQueue<Choice<SystemName * StatusItem, StatusItem>> = ConcurrentQueue()

    let private updateSystemStatuses systemName statuses =
        let currentStatuses = InternalState.currentSystemState()

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
            Choice1Of2 (systemName, status) |> Changes.queue.Enqueue
            InternalState.setSystemState systemName instance status
        )

    let private updateDomainStatus name status =
        let currentDomains = InternalState.currentDomainState()

        match currentDomains.TryFind name, status with
        | Some currentStatus, _ when currentStatus.Status = status -> ()
        | Some (Domain domain), status ->
            let updated = Domain { domain with Status = status }
            Choice2Of2 updated |> Changes.queue.Enqueue
            InternalState.setDomainState name updated
        | _ -> ()

    [<TailCall>]
    let rec private periodicallyCheckServices onStatusChange (logger: ILogger) (system: SoftwareSystem): PeriodicCheck = async {
        logger.LogDebug("Checking services")

        match! system |> ServiceCheck.checkSystemServices onStatusChange logger with
        | Ok statuses -> updateSystemStatuses system.Name statuses
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
        | Ok statuses -> updateSystemStatuses system.Name statuses
        | Error errors ->
            errors
            |> List.iter (fun (error: string) -> logger.LogError("Checking {system} resources ends with {error}", system.Name |> SystemName.value, error))

        logger.LogDebug("Wait for 1 hour ...")
        do! Async.Sleep (TimeSpan.FromHours 1)

        return! periodicallyCheckDataObjects onStatusChange logger system
    }

    [<TailCall>]
    let rec private periodicallyCheckDomain (logger: ILogger) (domain: DomainToCheck): PeriodicCheck = async {
        logger.LogDebug("Checking domain")

        match! domain |> ServiceCheck.checkDomain logger with
        | Ok status -> updateDomainStatus domain.Name status
        | Error error -> logger.LogError("Checking {domain} ends with {error}", domain.Name |> DomainName.value, error)

        logger.LogDebug("Wait for 15 seconds ...")
        do! Async.Sleep (TimeSpan.FromSeconds 15.)

        return! periodicallyCheckDomain logger domain
    }

    let registerSystem onStatusChange (loggerFactory: ILoggerFactory) (system: SoftwareSystem): PeriodicCheck list =
        InternalState.registerSystem system
        let logger = loggerFactory.CreateLogger($"StatusStorage<{system.Name}>")

        logger.LogDebug("Checking resources")
        [
            system |> periodicallyCheckDataObjects onStatusChange (loggerFactory.CreateLogger($"CheckDataObjects<{system.Name}>"))
            system |> periodicallyCheckServices onStatusChange (loggerFactory.CreateLogger($"CheckServices<{system.Name}>"))
        ]

    let registerNode onStatusChange (loggerFactory: ILoggerFactory) (node: StatusOf): PeriodicCheck list =
        match node with
        | StatusOf.System system -> registerSystem onStatusChange loggerFactory system
        | StatusOf.Domain domain ->
            InternalState.registerDomain domain
            let logger = loggerFactory.CreateLogger($"StatusStorage<{DomainName.value domain.Name}>")
            logger.LogDebug("Checking domain")
            [ domain |> periodicallyCheckDomain (loggerFactory.CreateLogger($"CheckDomain<{DomainName.value domain.Name}>")) ]

    let statuses (): StatusItem list = InternalState.allDomains () @ InternalState.all ()
    let systemStatuses (): StatusItem list =
        let domains = InternalState.allDomains ()

        let systems =
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

        domains @ systems

    let changes (): StatusItem list =
        let changeSet =
            Changes.queue.ToArray()
            |> Array.toList
            |> List.rev

        Changes.queue.Clear()

        let systemChanges =
            changeSet
            |> List.choose (function Choice1Of2 change -> Some change | Choice2Of2 _ -> None)
            |> List.distinctBy (fun (systemName, status) -> systemName, status.Name)
            |> List.groupBy fst
            |> List.map (fun (systemName, changes) ->
                System {
                    Name = systemName |> SystemName.value
                    Status = changes |> List.map snd |> Status.foldItems
                    Tags = []
                }
            )

        let domainChanges =
            changeSet
            |> List.choose (function Choice2Of2 status -> Some status | Choice1Of2 _ -> None)
            |> List.distinctBy (fun status -> status.Name)

        domainChanges @ systemChanges
