namespace WebSharper.WebSockets.Owin.Test

open WebSharper.Html.Server
open WebSharper
open WebSharper.Sitelets

type Action =
    | Home

module Skin =
    open System.Web

    type Page =
        {
            Title : string
            Body : list<Element>
        }

    let MainTemplate =
        Content.Template<Page>("~/Main.html")
            .With("title", fun x -> x.Title)
            .With("body", fun x -> x.Body)

    let WithTemplate title body : Content<Action> =
        Content.WithTemplate MainTemplate <| fun context ->
            {
                Title = title
                Body = body context
            }

module Site =

    let ( => ) text url =
        A [HRef url] -< [Text text]

    let Links (ctx: Context<Action>) =
        UL [
            LI ["Home" => ctx.Link Home]
        ]

    let HomePage ep =
        Skin.WithTemplate "HomePage" <| fun ctx ->
            [
                Div [Text "HOME"]
                Div [ClientSide <@ Client.WS ep @>]
                Links ctx
            ]

    let MainSitelet ep =
        Sitelet.Sum [
            Sitelet.Content "/" Home (HomePage ep)
        ]

module SelfHostedServer =

    open global.Owin
    open Microsoft.Owin.Hosting
    open Microsoft.Owin.StaticFiles
    open Microsoft.Owin.FileSystems
    open WebSharper.Owin
    open WebSharper.WebSockets.WebSocket

    [<EntryPoint>]
    let Main = function
        | [| rootDirectory; url |] ->
            use server = WebApp.Start(url, fun appB ->
                let ep = GetWebSocketEndPoint url "/ws"
                let options = Options.Create(rootDirectory)
                appB.UseStaticFiles(
                        StaticFileOptions(
                            FileSystem = PhysicalFileSystem(rootDirectory)))
                    .UseSitelet(rootDirectory, Site.MainSitelet ep)
                |> Server.Server "/ws" options.Json)
            stdout.WriteLine("Serving {0}", url)
            stdin.ReadLine() |> ignore
            0
        | _ ->
            eprintfn "Usage: WebSharper.WebSockets.Owin.Test ROOT_DIRECTORY URL"
            1
