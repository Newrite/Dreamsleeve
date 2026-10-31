namespace Dreamsleeve.Server.Web

open System
open System.Threading.Tasks
open Falco
open Falco.Routing
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Dreamsleeve.Server.Core

[<RequireQualifiedAccess>]
module PhantomRoutes =
    let private handle current upload : HttpHandler = fun context -> task {
        WebHost.noStore context
        // Capabilities travel in a header, never in access-log URLs.
        let authorization = context.Request.Headers.Authorization.ToString()
        let token = if authorization.StartsWith("Bearer ", StringComparison.Ordinal) then authorization.Substring 7 else ""
        if token.Length <> 64 || not (Seq.forall Uri.IsHexDigit token) then
            context.Response.StatusCode <- 403
        else
            match current() with
            | None -> context.Response.StatusCode <- 503
            | Some (port: PhantomHttpPort) ->
                let limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>()
                if not (isNull limit) && not limit.IsReadOnly then limit.MaxRequestBodySize <- Nullable(int64 PhantomOptions.defaults.Limits.CompressedBytes)
                let request = {
                    Token = token
                    Upload = upload
                    Length = if context.Request.ContentLength.HasValue then Some context.Request.ContentLength.Value else None
                    Body = if upload then context.Request.Body else context.Response.Body
                    BeginResponse = fun size ->
                        if not upload then
                            context.Response.ContentType <- "application/octet-stream"
                            context.Response.ContentLength <- Nullable(int64 size)
                    Cancellation = context.RequestAborted
                }
                let! result = port.Serve request

                match result with
                | Ok () -> if upload then context.Response.StatusCode <- 204
                | Error reason when not context.Response.HasStarted ->
                    context.Response.ContentLength <- Nullable()
                    context.Response.StatusCode <-
                        match reason with
                        | PhantomHttpError.Capability -> 403
                        | PhantomHttpError.Length -> 400
                        | PhantomHttpError.Canceled -> 409
                        | PhantomHttpError.Closed | PhantomHttpError.Truncated | PhantomHttpError.Io
                        | PhantomHttpError.StorageCompletion | PhantomHttpError.StorageLength | PhantomHttpError.Storage _ -> 503
                | Error _ -> context.Abort()
    }
    let endpoints current = [
        put "/phantoms/content" (handle current true)
        get "/phantoms/content" (handle current false)
    ]
