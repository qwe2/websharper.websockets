namespace WebSharper.WebSockets

open WebSharper

module WebSocket =
    open System.Collections
    open System.Collections.Concurrent
    open Fleck

    module private Async =
        let AwaitUnitTask (tsk : System.Threading.Tasks.Task) =
            tsk.ContinueWith(ignore) |> Async.AwaitTask

    [<JavaScript>]
    type Endpoint<'T> =
        private {
            URI : string
        }

        static member FromUri (uri : string) =
            { URI = uri }

    module private MessageCoder =
        module J = WebSharper.Core.Json

        let private jP = J.Provider.Create()
        let ToJString (msg: 'T) =
            let enc = jP.GetEncoder<'T>()
            enc.Encode msg
            |> jP.Pack
            |> J.Stringify

        let FromJString str : 'T =
            let dec = jP.GetDecoder<'T>()
            J.Parse str
            |> dec.Decode

    type private InternalMessage<'T> =
        | Message of IWebSocketConnectionInfo * 'T
        | Error of IWebSocketConnectionInfo * exn
        | Close of IWebSocketConnectionInfo
        | Open of IWebSocketConnectionInfo * IWebSocketConnection

    type Action<'T> =
        | Message of 'T
        | Close
    
    type WebSocketClient<'T> =
        {
            ConnectionInfo : IWebSocketConnectionInfo
            ReplyChan      : AsyncReplyChannel<Action<'T>>
        }

    let private mkWSClient ci rc = { ConnectionInfo = ci; ReplyChan = rc }

    [<JavaScript>]
    type Message<'T> =
        | Message of 'T
        | Error of exn
        | Open of MailboxProcessor<Action<'T>>
        | Close

    [<JavaScript>]
    module Client =
        open WebSharper.JavaScript

        let private processResponse (sock : WebSocket) msg =
            async {
                // TODO: can this be done in a nicer way?
                while sock.ReadyState <> WebSocketReadyState.Open do
                    do! Async.Sleep 10
                let! msg = msg
                match msg with
                | Action.Message value ->
                    value 
                    |> Json.Stringify
                    |> sock.Send
                | Action.Close -> sock.Close ()
            }

        type private State =
            | Open
            | Closed

        type private ServerStatus<'T> =
            {
                State : State
                Queue : Action<'T> list
            }

        type private ServerMessage<'T> =
            | Open
            | Close
            | Message of Action<'T>

        let ConnectTo (endpoint : Endpoint<'T>) (agent : MailboxProcessor<Message<'T> * AsyncReplyChannel<Action<'T>>>) =
            let socket = new WebSocket(endpoint.URI)

            let proc (msg : Message<'T>) =
                agent.PostAndAsyncReply (fun chan -> msg, chan)
                |> processResponse socket
                |> Async.Start

            let internalServer =
                MailboxProcessor.Start <| fun inbox ->
                    let rec loop (st : ServerStatus<'T>) : Async<unit> =
                        async {
                            let! msg = inbox.Receive ()
                            match msg with
                            | Open ->
                                for a in st.Queue do
                                    do! processResponse socket <| async.Return a
                                return! loop { State = State.Open; Queue = [] }
                            | Close ->
                                return! loop { st with State = State.Closed }
                            | Message msg ->
                                match st.State with
                                | State.Open -> 
                                    do! processResponse socket (async.Return msg)
                                    return! loop st
                                | State.Closed ->
                                    return! loop { st with Queue = msg :: st.Queue }
                        }
                    loop { State = State.Closed; Queue = [] }

            let server = MailboxProcessor.Start <| fun inbox ->
                let rec loop () : Async<unit> =
                    async {
                        let! msg = inbox.Receive ()
                        internalServer.Post <| ServerMessage.Message msg
                        return! loop ()
                    }
                loop ()

            socket.Onopen <- fun () -> 
                internalServer.Post Open
                proc <| Message.Open server
            socket.Onclose <- fun () -> 
                internalServer.Post Close
                proc <| Message.Close
            socket.Onmessage <- fun msg -> 
                As<string> msg.Data |> Json.Parse |> As |> Message.Message |> proc
            socket.Onerror <- fun () -> System.Exception "error" |> Message.Error |> proc

            server

    let private mailBox initState computeState = 
        MailboxProcessor.Start <| fun inbox ->
            let dict = new ConcurrentDictionary<IWebSocketConnection, WebSocketClient<'T>>()

            let rec loop () = async {
                let! (msg, replyChan) = inbox.Receive()
                let! nstate = computeState msg replyChan dict
                return! loop nstate
            }
            loop initState
    
    let private processResponse (sock : IWebSocketConnection) msg =
        async {
            let! msg = msg
            match msg with
            | Action.Message value ->
                let msg = MessageCoder.ToJString value
                do! sock.Send msg |> Async.AwaitUnitTask
            | Action.Close -> sock.Close ()
        }

    let private newClientProcessor (sock : IWebSocketConnection) = 
        MailboxProcessor.Start <| fun inbox ->
            let rec loop () = async {
                do! processResponse sock <| inbox.Receive()
                return! loop ()
            }
            loop ()

    let StartWebSocketServer href port (agent : MailboxProcessor<WebSocketClient<'T> * Message<'T>>) =
        let processor = mailBox () <| fun msg replyChan clients ->
            async {
                let mk ci msg = agent.Post <| (mkWSClient ci replyChan, msg)

                match msg with
                | InternalMessage.Open (ci, socket) -> ci, Message.Open <| newClientProcessor socket
                | InternalMessage.Close ci -> ci, Message.Close
                | InternalMessage.Message (ci, msg) -> ci, Message.Message msg
                | InternalMessage.Error (ci, ex) -> ci, Message.Error ex
                |> (fun (a,b) -> mk a b)
            }

        let server = new WebSocketServer(sprintf "%s:%d" href port)
        server.Start(fun socket -> 
            let proc msg =
                processor.PostAndAsyncReply (fun chan -> msg, chan)
                |> processResponse socket
                |> Async.Start

            socket.OnOpen <- fun () -> proc <| InternalMessage.Open (socket.ConnectionInfo, socket)
            socket.OnClose <- fun () -> proc <| InternalMessage.Close socket.ConnectionInfo
            socket.OnMessage <- fun msg -> 
                let m = MessageCoder.FromJString msg
                proc <| InternalMessage.Message (socket.ConnectionInfo, m)
            socket.OnError <- fun ex -> 
                proc <| InternalMessage.Error (socket.ConnectionInfo, ex)
        )

        { URI = sprintf "ws://localhost:%d" port } : Endpoint<'T>
    

//module Test =
//    open WebSocket
//    open IntelliFactory.WebSharper
//    open IntelliFactory.WebSharper.JavaScript
//
//    open Fleck
//
//    module Cl = IntelliFactory.WebSharper.Html.Client.Default
//
//    let Server =
//        let wrtln (x : string) = System.Diagnostics.Debug.WriteLine x
//
//        StartWebSocketServer "ws://0.0.0.0" 51000 <| MailboxProcessor.Start(fun inbox ->
//            let rec loop (clients: Map<int, MailboxProcessor<Action<string>>>) =
//                async {
//                    let! ({ConnectionInfo = ci; ReplyChan = rc}, msg) = inbox.Receive ()
//                    let nstate =
//                        match msg with
//                        | Message data -> 
//                            if data = "haha" then Action.Message "LOL"
//                            else Action.Close
//                            |> rc.Reply
//
//                            clients
//                        | Open client -> 
//                            clients
//                            |> Map.iter (fun k _ ->
//                                if k <> ci.GetHashCode() then client.Post <| Action.Message (string k)
//                            )
//
//                            wrtln <| sprintf "New client on IP %s: %s" ci.ClientIpAddress (ci.Id.ToString())
//                            clients |> Map.add (ci.GetHashCode()) client
//                        | Close -> 
//                            wrtln <| sprintf "Client disconected on IP %s: %s" ci.ClientIpAddress (ci.Id.ToString())
//                            clients |> Map.remove (ci.GetHashCode())
//                        | Error exn -> 
//                            wrtln <| sprintf "Panic! %s" exn.Message
//                            clients
//
//                    return! loop nstate
//                }
//            loop Map.empty
//        )
//
//    [<JavaScript>]
//    module Client =
//        let Main (e : Endpoint<string>) =
//            let server =
//                Client.ConnectTo e <| MailboxProcessor.Start(fun inbox ->
//                    let rec loop () : Async<unit> =
//                        async {
//                            let! (msg, rc) = inbox.Receive ()
//                            match msg with
//                            | Message (data : string) ->
//                                Console.Log("got this:", data)
//                            | Close -> 
//                                Console.Log "Connection closed."
//                            | Open _server ->
//                                Console.Log "WebSocket conenction open."
//                                rc.Reply Action.Close
//                            | Error ex ->
//                                Console.Log ex.Message
//                            return! loop ()
//                        }
//                    loop ()
//                )
//            server.Post <| Action.Message "haha"
//            Cl.Text ""
