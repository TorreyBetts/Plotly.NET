namespace Plotly.NET.Verso

open Plotly.NET
open Verso.Abstractions

module Formatters =

    /// The MIME type Verso reserves for a self-contained document that draws itself once it is
    /// running in a browser, as opposed to a fragment pasted into the notebook page.
    ///
    /// A chart has to travel this way. Verso inserts <c>text/html</c> output as markup, and markup
    /// inserted that way never runs the script it carries, so the drawing code in a chart fragment
    /// would never execute. Output of this type is given a frame of its own, where the script runs
    /// and the document reports back how tall it turned out.
    [<Literal>]
    let VersoWidgetMimeType = "text/x-verso-widget"

    /// Converts a chart to a document that loads plotly.js from the CDN.
    ///
    /// This is the default because the document stays small, which matters here: cell output is
    /// written into the notebook file, so an embedded copy of plotly.js is saved once per chart.
    let toVersoHTML gChart =
        gChart
        |> Chart.withDisplayOptionsStyle (
            PlotlyJSReference = CDN $"https://cdn.plot.ly/plotly-{Globals.PLOTLYJS_VERSION}.min.js"
        )
        |> GenericChart.toEmbeddedHTML

    /// Converts a chart to a document with plotly.js embedded inline, for machines with no route to
    /// the CDN. The document runs to about 3.5 MB, and cell output is saved with the notebook, so
    /// that is the file size cost per chart.
    let toVersoHTMLOffline gChart =
        gChart
        |> Chart.withDisplayOptionsStyle (PlotlyJSReference = Full)
        |> GenericChart.toEmbeddedHTML

    /// Converts a chart to the cell output Verso displays, having first applied the notebook's
    /// theme and let the chart size itself to the cell.
    let toVersoOutput (theme: IThemeContext) gChart =
        gChart
        |> Theming.forDisplay theme
        |> toVersoHTML
        |> fun html -> CellOutput(VersoWidgetMimeType, html)

    /// As <c>toVersoOutput</c>, with plotly.js embedded inline rather than loaded from the CDN.
    let toVersoOutputOffline (theme: IThemeContext) gChart =
        gChart
        |> Theming.forDisplay theme
        |> toVersoHTMLOffline
        |> fun html -> CellOutput(VersoWidgetMimeType, html)
