namespace Plotly.NET.Verso

open System
open System.Collections.Generic
open System.Threading.Tasks
open Verso.Abstractions
open Plotly.NET

/// Mutable settings for chart display in Verso, in the same spirit as <c>Plotly.NET.Defaults</c>:
/// assign to them from a notebook cell and every chart displayed afterwards follows.
module VersoDefaults =

    /// Whether to embed plotly.js in each chart rather than load it from the CDN. Turn this on for
    /// a machine with no route to the CDN, bearing in mind that it adds roughly 3.5 MB to the
    /// notebook file per chart, since cell output is saved with the notebook. Default: false.
    let mutable EmbedPlotlyJS = false

[<VersoExtension>]
type GenericChartFormatter() =

    interface IExtension with
        member _.ExtensionId = "net.plotly.plotly-net-verso"
        member _.Name = "Plotly.NET"
        member _.Version = "1.0.0"
        member _.Author = "Plotly.NET Contributors"
        member _.Description = "Renders Plotly.NET GenericChart objects as interactive charts in Verso notebooks."
        member _.OnLoadedAsync(_ctx) = Task.CompletedTask
        member _.OnUnloadedAsync() = Task.CompletedTask

    interface IDataFormatter with
        member _.SupportedTypes: IReadOnlyList<Type> =
            [| typeof<GenericChart> |] :> IReadOnlyList<Type>

        member _.Priority = 100

        member _.CanFormat(value, _ctx) = value :? GenericChart

        member _.FormatAsync(value, ctx) =
            let chart = value :?> GenericChart

            let output =
                if VersoDefaults.EmbedPlotlyJS then
                    Formatters.toVersoOutputOffline ctx.Theme chart
                else
                    Formatters.toVersoOutput ctx.Theme chart

            Task.FromResult output
