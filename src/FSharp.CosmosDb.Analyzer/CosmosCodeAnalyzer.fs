namespace FSharp.CosmosDb.Analyzer

open FSharp.Compiler.Range
open Azure.Cosmos
open FSharp.Control
open FSharp.Analyzers.SDK
open System.Net.Http
open System

type ConnectionResult =
    | Error of string
    | Success of CosmosClient

module CosmosCodeAnalyzer =
    let testConnection host key =
        let client = new CosmosClient(host, key, CosmosClientOptions())

        try
            client.ReadAccountAsync()
            |> Async.AwaitTask
            |> Async.RunSynchronously
            |> ignore
            Success client
        with
        | :? AggregateException as ex when ex.InnerExceptions |> Seq.exists (fun e -> e :? HttpRequestException) ->
            Error "Could not establish Cosmos DB connection."
        | ex ->
            printfn "%A" ex
            Error "Something unknown happened when trying to access Cosmos DB"

    let findDatabaseOperation (operation: CosmosOperation) =
        operation.blocks
        |> List.tryFind (function
            | CosmosAnalyzerBlock.DatabaseId(_) -> true
            | _ -> false)
        |> Option.map (function
            | CosmosAnalyzerBlock.DatabaseId(databaseId, range) -> (databaseId, range)
            | _ -> failwith "No database operation")

    let analyzeDatabaseOperation databaseId (range: range) (cosmosClient: CosmosClient) =
        async {
            let! result = cosmosClient.GetDatabaseQueryIterator<DatabaseProperties>()
                          |> AsyncSeq.ofAsyncEnum
                          |> AsyncSeq.toListAsync

            let matching = result |> List.exists (fun db -> db.Id = databaseId)

            return if matching then
                       []
                   else
                       let msg = Messaging.warning (sprintf "The database '%s' was not found." databaseId) range

                       let fixes =
                           result
                           |> List.map (fun prop ->
                               { FromRange = range
                                 FromText = databaseId
                                 ToText = prop.Id })

                       [ { msg with Fixes = fixes } ]
        }
        |> Async.RunSynchronously

    let findContainerOperation (operation: CosmosOperation) =
        operation.blocks
        |> List.tryFind (function
            | CosmosAnalyzerBlock.ContainerName(_) -> true
            | _ -> false)
        |> Option.map (function
            | CosmosAnalyzerBlock.ContainerName(containerName, range) -> (containerName, range)
            | _ -> failwith "No container name operation")

    let analyzeContainerNameOperation databaseId containerName (range: range) (cosmosClient: CosmosClient) =
        async {
            try
                let! result = cosmosClient.GetDatabase(databaseId).GetContainerQueryIterator<ContainerProperties>()
                              |> AsyncSeq.ofAsyncEnum
                              |> AsyncSeq.toListAsync
                let matching = result |> List.exists (fun containerProps -> containerProps.Id = containerName)

                return if matching then
                           []
                       else
                           let msg =
                               Messaging.warning (sprintf "The container name '%s' was not found." containerName) range

                           let fixes =
                               result
                               |> List.map (fun prop ->
                                   { FromRange = range
                                     FromText = containerName
                                     ToText = prop.Id })

                           [ { msg with Fixes = fixes } ]
            with
            | :? AggregateException as ex when ex.InnerExceptions |> Seq.exists (fun e -> e :? CosmosException) ->
                return [ Messaging.warning "Failed to retrieve container names, database name is probably invalid."
                             range ]
            | ex ->
                printfn "%O" ex
                return [ Messaging.error "Fatal error talking to Cosmos DB" range ]
        }
        |> Async.RunSynchronously
