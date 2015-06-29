namespace WebSharper.WebSockets

open WebSharper

module WebSocket =
    open Microsoft.Practices.ServiceLocation
    open global.Owin
    open Owin.WebSocket
    open Owin.WebSocket.Extensions

    module private Async =
        let AwaitUnitTask (tsk : System.Threading.Tasks.Task) =
            tsk.ContinueWith(ignore) |> Async.AwaitTask

    [<JavaScript>]
    type Endpoint<'T> =
        private {
            URI : string
        }

    type Endpoint =
        static member FromUri (uri : string) =
            { URI = uri }

    module MessageCoder =
        module J = WebSharper.Core.Json

        let ToJString (jP: J.Provider) (msg: 'T) =
            let enc = jP.GetEncoder<'T>()
            enc.Encode msg
            |> jP.Pack
            |> J.Stringify

        let FromJString (jP: J.Provider) str : 'T =
            let dec = jP.GetDecoder<'T>()
            J.Parse str
            |> dec.Decode

    type private InternalMessage<'T> =
        | Message of WebSocketConnection * 'T
        | Error of WebSocketConnection * exn
        | Close of WebSocketConnection
        | Open of WebSocketConnection

    type Action<'T> =
        | Message of 'T
        | Close
    
    [<ReferenceEquality>]
    type WebSocketClient<'T> =
        {
            Conn           : WebSocketConnection
            ReplyChan      : AsyncReplyChannel<Action<'T>>
        }

    let private mkWSClient conn rc = { Conn = conn; ReplyChan = rc }

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
//                while sock.ReadyState <> WebSocketReadyState.Open do
//                    do! Async.Sleep 10
                let! msg = msg
                match msg with
                | Action.Message (value : 'T) ->
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

        let ConnectTo (endpoint : Endpoint<'T>) 
            (agent : MailboxProcessor<Message<'T> * AsyncReplyChannel<Action<'T>>>) =

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
                As<string> msg.Data |> Json.Parse |> Json.Activate |> Message.Message |> proc
            socket.Onerror <- fun () -> System.Exception "error" |> Message.Error |> proc

            server

    type private WebSocketProcessor<'T>
        (agent : MailboxProcessor<WebSocketClient<'T> option * Message<'T>>,
         jP: Core.Json.Provider) =

        let processResponse (sock : WebSocketConnection) msg =
            async {
                let! msg = msg
                match msg with
                | Action.Message value ->
                    let msg = MessageCoder.ToJString jP value
                    let bytes = System.Text.Encoding.UTF8.GetBytes(msg)
                    do! sock.SendText(bytes, true) |> Async.AwaitUnitTask
                | Action.Close -> 
                    do! sock.Close (System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                                        "Client requested.") |> Async.AwaitUnitTask
            }

        let newClientProcessor (sock : WebSocketConnection) = 
            MailboxProcessor.Start <| fun inbox ->
                let rec loop () = async {
                    do! processResponse sock <| inbox.Receive()
                    return! loop ()
                }
                loop ()
        
        let mailbox = 
            MailboxProcessor.Start <| fun inbox ->
                async {
                    while true do
                        let! (msg, replyChan) = inbox.Receive()
                        let conn, imsg =
                            match msg with
                            | InternalMessage.Open socket -> socket, Message.Open <| newClientProcessor socket
                            | InternalMessage.Close soscket -> soscket, Message.Close
                            | InternalMessage.Message (socket, msg) -> socket, Message.Message msg
                            | InternalMessage.Error (socket, ex) -> socket, Message.Error ex
                        agent.Post <| (Some <| mkWSClient conn replyChan, imsg) 
                }   
                
        member this.Mailbox = mailbox
        member this.ProcessResponse socket msg = processResponse socket msg
        member this.JsonProvider = jP

    type private ProcessWebSocketonnection<'T>
        (processor : WebSocketProcessor<'T>) =

        inherit WebSocketConnection()

        let proc socket msg =
            processor.Mailbox.PostAndAsyncReply (fun chan -> msg, chan)
            |> processor.ProcessResponse socket
            |> Async.Start

        override x.OnClose(status, desc) =
            proc x <| InternalMessage.Close x

        override x.OnOpen() =
            proc x <| InternalMessage.Open x

        override x.OnMessageReceived(message, typ) =
            async {
                let json = System.Text.Encoding.UTF8.GetString(message.Array)
                let m = MessageCoder.FromJString processor.JsonProvider json
                proc x <| InternalMessage.Message (x, m)
            }
            |> Async.StartAsTask :> _

        override x.OnReceiveError(ex) = 
            proc x <| InternalMessage.Error (x, ex)

    type private WebSocketServiceLocator<'T>(processor : WebSocketProcessor<'T>) =
        interface IServiceLocator with

            member x.GetService(typ) =
                raise <| System.NotImplementedException()

            member x.GetInstance(t : System.Type) =
                let ctor = t.GetConstructor([| processor.GetType() |])
                ctor.Invoke([| processor |])

            member x.GetInstance(t, key) =
                raise <| System.NotImplementedException()

            member x.GetInstance<'TService>() =
                let t = typeof<'TService>
                let ctor = t.GetConstructor([| processor.GetType() |])
                ctor.Invoke([| processor |]) :?> 'TService

            member x.GetInstance<'TService>(key : string) : 'TService =
                raise <| System.NotImplementedException()

            member x.GetAllInstances(t) =
                raise <| System.NotImplementedException()

            member x.GetAllInstances<'TService>() : System.Collections.Generic.IEnumerable<'TService> =
                raise <| System.NotImplementedException()

    let GetWebSocketEndPoint (url : string) (route : string) =
        let uri = System.Uri(System.Uri(url), route)
        let wsuri = sprintf "ws://%s%s" uri.Authority uri.AbsolutePath
        Endpoint.FromUri<'T> wsuri
    
    let StartWebSocketServer (route : string) (builder : IAppBuilder) (json: Core.Json.Provider)
        (agent : MailboxProcessor<WebSocketClient<'T> option * Message<'T>>) =

        let processor = WebSocketProcessor(agent, json)

        builder.MapWebSocketRoute<ProcessWebSocketonnection<'T>>(route, WebSocketServiceLocator<'T>(processor))

