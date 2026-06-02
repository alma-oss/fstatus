namespace Alma.Status

open System
open Alma.ServiceIdentification
open Feather.ErrorHandling
open Alma.Status.Common

type StatusResourceKind =
    | ServiceResource
    | DataObjectResource

type StatusChange = {
    ResourceKind: StatusResourceKind
    Instance: Instance
    Status: Status
}

type OnStatusChange = StatusChange -> unit

[<RequireQualifiedAccess>]
module OnStatusChange =
    open Alma.Metrics

    type Resource = Resource of (ResourceType -> Instance -> ResourceAvailability)

    let private enable currentInstance resourceAvailability =
        resourceAvailability
        |> ResourceAvailability.enable currentInstance
        |> ignore

    let private disable currentInstance resourceAvailability =
        resourceAvailability
        |> ResourceAvailability.disable currentInstance
        |> ignore

    let resourceAvailability (Resource resource) currentInstance: OnStatusChange =
        fun statusChange ->
            let resourceInstance = statusChange.Instance
            let resourceType =
                match statusChange.ResourceKind with
                | ServiceResource -> ResourceType "service"
                | DataObjectResource -> ResourceType "dataObject"

            match statusChange.Status with
            | Status.Normal _
            | Status.Info _ -> resource resourceType resourceInstance |> enable currentInstance
            | Status.Warning _
            | Status.Critical _ -> resource resourceType resourceInstance |> disable currentInstance

[<RequireQualifiedAccess>]
module StatusMessage =
    let create message = {
        DateTime = DateTimeOffset.Now
        Message = Some message
        Note = None
    }

[<AutoOpen>]
module PublicMessage =
    [<Literal>]
    let PublicInfoIdle = "Idle"

    [<Literal>]
    let PublicErrorUnauthorized = "Unauthorized"

    [<Literal>]
    let PublicErrorForbidden = "Forbidden"

    [<Literal>]
    let PublicErrorUnhealthy = "Unhealthy"

    [<Literal>]
    let PublicErrorUnhealthyWithLag = "Unhealthy with lag"

    [<Literal>]
    let PublicErrorHealthyWithLag = "Healthy with lag"

type CheckName = CheckName of string

type CheckMessage = { Message: string option; Note: string option }

type CheckResult =
    | Success of CheckMessage
    | Info of CheckMessage
    | Warning of CheckMessage
    | Critical of CheckMessage

type Check = { Name: CheckName; Execute: Async<CheckResult> }

type StatusPageSource =
    | K8sInternalService of path: string
    | Url of string
    | NoStatusPage

type ServiceToCheck = {
    Name: string
    Instance: Instance
    Tags: Tag list
    StatusPage: StatusPageSource
    Checks: Check list
}

type DataObjectToCheck = {
    Name: string
    Instance: Instance
    Tags: Tag list
    Checks: Check list
    Details: Detail list
} // todo - there is no other type/idea for Details here yet

type SystemName = SystemName of string

type SoftwareSystem = {
    Name: SystemName
    Service: Service
    Tags: Tag list
    Services: ServiceToCheck list
    DataObjects: DataObjectToCheck list
}

[<RequireQualifiedAccess>]
module CheckName =
    let value (CheckName name) = name

    let ofInstance baseName instance =
        sprintf "%s--%s" (instance |> Instance.concat "-") baseName |> CheckName

[<RequireQualifiedAccess>]
module CheckMessage =
    let empty = { Message = None; Note = None }

[<RequireQualifiedAccess>]
module CheckResult =
    let private statusMessage (message: CheckMessage) : StatusMessage = {
        DateTime = DateTimeOffset.Now
        Message = message.Message
        Note = message.Note
    }

    let toStatus: CheckResult -> Status = function
        | Success message -> Status.Normal (statusMessage message)
        | Info message -> Status.Info (statusMessage message)
        | Warning message -> Status.Warning (statusMessage message)
        | Critical message -> Status.Critical (statusMessage message)

    let success = Success CheckMessage.empty

    let info message =
        Info { Message = Some message; Note = None }

    let warning message =
        Warning { Message = Some message; Note = None }

    let critical message =
        Critical { Message = Some message; Note = None }

[<RequireQualifiedAccess>]
module Check =
    let execute (check: Check) = check.Execute

    let map f (check: Check) = { check with Execute = check.Execute |> Async.map f }

    let success name = {
        Name = name
        Execute = async { return CheckResult.success }
    }

[<RequireQualifiedAccess>]
module SystemName =
    let value (SystemName name) = name

[<RequireQualifiedAccess>]
module SoftwareSystem =
    let name ({ Name = name }: SoftwareSystem) = name

    let mapServices f (system: SoftwareSystem) = {
        system with
            Services = system.Services |> List.map (f system)
    }

    let mapDataObjects f (system: SoftwareSystem) = {
        system with
            DataObjects = system.DataObjects |> List.map (f system)
    }

[<RequireQualifiedAccess>]
module ServiceToCheck =
    let mapChecks f (service: ServiceToCheck) = {
        service with
            Checks = service.Checks |> List.map (f service)
    }

[<RequireQualifiedAccess>]
module DataObjectToCheck =
    let mapChecks f (dataObject: DataObjectToCheck) = {
        dataObject with
            Checks = dataObject.Checks |> List.map (f dataObject)
    }

type Severity = Check -> Check

[<RequireQualifiedAccess>]
module Severity =
    open Alma.State.ConcurrentStorage

    /// Consider critical result as warning, since it is not a critical component
    let notCritical: Severity =
        Check.map (function
            | Critical message -> Warning message
            | message -> message
        )

    let private firstCriticalCheck: State<CheckName, DateTimeOffset> = State.empty ()

    let criticalAfterTime (time: TimeSpan): Severity = fun check ->
        check |> Check.map (function
            | Success message ->
                firstCriticalCheck |> State.tryRemove (Key check.Name)
                Success message

            | Info message ->
                firstCriticalCheck |> State.tryRemove (Key check.Name)
                Info message

            | Warning message ->
                firstCriticalCheck |> State.tryRemove (Key check.Name)
                Warning message

            | Critical message ->
                match firstCriticalCheck |> State.tryFind (Key check.Name) with
                | Some firstCriticalOccurredAt ->
                    let note = sprintf "Considered critical from %s" (firstCriticalOccurredAt + time |> DateTimeOffset.pretty)
                    if DateTimeOffset.Now - firstCriticalOccurredAt > time
                    then Critical { message with Note = Some note }
                    else Warning { message with Note = Some note }

                | None ->
                    let now = DateTimeOffset.Now
                    firstCriticalCheck |> State.set (Key check.Name) now

                    Warning { message with Note = Some <| sprintf "Considered critical after %s" (now + time |> DateTimeOffset.pretty) }
        )
