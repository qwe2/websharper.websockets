namespace WebSharper.WebSockets.Owin.Test

module Server =
    open WebSharper.WebSockets.WebSocket

    type Message =
        | Request of string
        | Response of string

    let Server route json builder =
        let wrtln (x : string) = System.Diagnostics.Debug.WriteLine x

        let proc = MailboxProcessor.Start(fun inbox ->
            async {
                while true do
                    let! (cl, msg) = inbox.Receive ()
                    let a = msg.ToString ()
                    match (msg, cl) with
                    | Message data, Some cl -> 
                        match data with
                        | Request x ->
                            cl.ReplyChan.Reply <| Action.Message (Response x)
                        | _ -> ()
                    | Message data, _ -> 
                        ()
                    | Error exn, _ -> 
                        wrtln <| sprintf "Panic! %s" exn.Message
                    | Open a, _ -> ()
                    | Close, _ -> ()
                    | _ -> ()
            }
        )

        StartWebSocketServer route builder json proc