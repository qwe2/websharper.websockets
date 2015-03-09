namespace WebSharper.WebSockets.Test

open WebSharper
open WebSharper.JavaScript
open WebSharper.Html.Client
open WebSharper.WebSockets

module W = WebSocket

[<JavaScript>]
type Msg = 
    {
        Time : System.DateTime
        Payload : string
    }

    with
        [<JavaScript>]
        static member mk py = { Time = System.DateTime.Now; Payload = py }

[<JavaScript>]
module Client =
    let Main (ep : W.Endpoint<Msg>) =
        Div []
        |>! OnAfterRender (fun el ->
            let server =
                W.Client.ConnectTo ep <| MailboxProcessor.Start (fun inbox ->
                    let rec loop () : Async<unit> = 
                        async {
                            let! msg = inbox.Receive()
                            Console.Log msg
                            return! loop ()
                        }
                    loop ()
                )
            Console.Log "hello"
            server.Post <| W.Action.Message (Msg.mk "HELLO")
            Console.Log "bello"
            )
        


module Server =
    let Server =
        let wrtln (x : string) = System.Diagnostics.Debug.WriteLine x

        W.StartWebSocketServer "ws://0.0.0.0" 51000 <| MailboxProcessor.Start(fun inbox ->
            let rec loop (clients: Map<int, MailboxProcessor<W.Action<Msg>>>) =
                async {
                    let! ({ConnectionInfo = ci; ReplyChan = rc}, msg) = inbox.Receive ()
                    let nstate =
                        match msg with
                        | W.Message.Message data -> 
                            if data.Payload = "HELLO" then W.Action.Message <| Msg.mk "LOL"
                            else W.Action.Close
                            |> rc.Reply

                            clients
                        | W.Message.Open client -> 
                            clients
                            |> Map.iter (fun k _ ->
                                if k <> ci.GetHashCode() then client.Post <| W.Action.Message (Msg.mk (string k))
                            )

                            wrtln <| sprintf "New client on IP %s: %s" ci.ClientIpAddress (ci.Id.ToString())
                            clients |> Map.add (ci.GetHashCode()) client
                        | W.Message.Close -> 
                            wrtln <| sprintf "Client disconected on IP %s: %s" ci.ClientIpAddress (ci.Id.ToString())
                            clients |> Map.remove (ci.GetHashCode())
                        | W.Message.Error exn -> 
                            wrtln <| sprintf "Panic! %s" exn.Message
                            clients

                    return! loop nstate
                }
            loop Map.empty
        )

type ClientControl() =
    inherit Web.Control()

    let ep = Server.Server

    [<JavaScript>]
    override this.Body =
        Client.Main ep :> _