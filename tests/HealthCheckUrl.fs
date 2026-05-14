module Alma.Status.Tests.HealthCheckUrl

open Expecto
open Alma.Status.HealthCheck
open Alma.ServiceIdentification
open Alma.WebApplication.Http

[<Tests>]
let urlCreationTests =
    let instance =
        Instance.createFromValues
            (Domain "test")
            (Context "monitoring")
            (Purpose "redis")
            (Version "v1")

    testList "URL creation" [
        testList "localUrlWithPath" [
            testCase "trims leading slash" <| fun _ ->
                let (Url url) = localUrlWithPath instance "/health"

                Expect.stringContains url "/health" "Path should be appended once"
                Expect.isFalse (url.Contains "//health") "Path should not contain double slash"

            testCase "accepts path without leading slash" <| fun _ ->
                let (Url url) = localUrlWithPath instance "health"

                Expect.stringContains url "/health" "Path should be appended"

            testCase "supports empty path" <| fun _ ->
                let (Url url) = localUrlWithPath instance ""

                Expect.isTrue (url.EndsWith "/") "Empty path should keep trailing slash"
        ]

        testList "k8sServiceAtPort" [
            testCase "includes selected port" <| fun _ ->
                let (Url url) = k8sServiceAtPort 8080 instance "/metrics"

                Expect.stringContains url ":8080/" "URL should contain requested port"
                Expect.stringContains url "metrics" "URL should contain requested path"

            testCase "trims leading slash in path" <| fun _ ->
                let (Url url) = k8sServiceAtPort 9090 instance "/status"

                Expect.isFalse (url.Contains "//status") "Path should not contain double slash"

            testCase "supports empty path" <| fun _ ->
                let (Url url) = k8sServiceAtPort 9090 instance ""

                Expect.isTrue (url.EndsWith "/") "Empty path should keep trailing slash"
        ]

        testList "k8sSTSPodsAtPort" [
            testCase "returns expected pod count" <| fun _ ->
                let urls = k8sSTSPodsAtPort 6379 3 instance "/info"

                Expect.hasLength urls 3 "Should create URL for each pod index"

            testCase "returns empty list for zero pods" <| fun _ ->
                let urls = k8sSTSPodsAtPort 6379 0 instance "/info"

                Expect.isEmpty urls "Zero pod count should produce no URLs"

            testCase "contains pod indexes and path" <| fun _ ->
                let urls = k8sSTSPodsAtPort 6379 2 instance "info"
                let (Url first) = urls[0]
                let (Url second) = urls[1]

                Expect.stringContains first "-0." "First URL should target pod 0"
                Expect.stringContains second "-1." "Second URL should target pod 1"
                Expect.stringContains first "/info" "Path should be appended"
                Expect.isFalse (first.Contains "//info") "Path should not contain double slash"
        ]
    ]
