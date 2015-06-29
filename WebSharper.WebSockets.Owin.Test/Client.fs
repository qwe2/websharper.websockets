namespace WebSharper.WebSockets.Owin.Test

open WebSharper
open WebSharper.JavaScript
open WebSharper.Html.Client
open WebSharper.WebSockets.WebSocket

[<JavaScript>]
module Client =

    let WS (endpoint : Endpoint<Server.Message>) =

        let server =
            Client.ConnectTo endpoint <| MailboxProcessor.Start(fun inbox ->
                let rec loop () : Async<unit> =
                    async {
                        let! (msg, rc) = inbox.Receive ()
                        match msg with
                        | Message data ->
                            match data with
                            | Server.Response x -> Console.Log x
                            | _ -> ()
                        | Close -> 
                            Console.Log "Connection closed."
                        | Open server ->
                            Console.Log "WebSocket conenction open."
                        | Error ex ->
                            Console.Log ex.Message
                        return! loop ()
                    }
                loop ()
            )

        async {
            while true do
                do! Async.Sleep 1000
                server.Post <| Action.Message (Server.Request "HELLO")
        }
        |> Async.Start

        Text ""
