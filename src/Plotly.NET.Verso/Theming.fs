namespace Plotly.NET.Verso

open System
open DynamicObj
open Giraffe.ViewEngine
open Newtonsoft.Json
open Plotly.NET
open Plotly.NET.ConfigObjects
open Plotly.NET.LayoutObjects
open Verso.Abstractions

/// Adapts a chart to the notebook page it is about to appear on: colors taken from the active
/// theme, and a size that follows the cell instead of a fixed pixel box.
module Theming =

    /// The library default template, serialized once so charts can be compared against it.
    ///
    /// A chart carrying this template has not been given one by its author, which is the only case
    /// where substituting a theme-derived template is appropriate.
    let private defaultTemplateJson =
        lazy (JsonConvert.SerializeObject(Defaults.DefaultTemplate, Globals.JSON_CONFIG))

    /// Whether the chart still carries the library default template.
    ///
    /// The default is deep-copied into each chart at creation, so the reference is never shared and
    /// the serialized forms are compared instead. A comparison that cannot be made is reported as
    /// author-supplied, which errs towards leaving the chart as it was written.
    let private usesDefaultTemplate (layout: Layout) =
        try
            match layout.TryGetTypedPropertyValue<DynamicObj>("template") with
            | Some template ->
                JsonConvert.SerializeObject(template, Globals.JSON_CONFIG) = defaultTemplateJson.Value
            | None -> true
        with _ ->
            false

    /// A template whose surfaces, text, and gridlines come from the notebook theme.
    ///
    /// Surface and text are read from the same theme, so the two are always legible against each
    /// other whichever theme is active. The color way is deliberately left alone: those colors
    /// carry the meaning of the data, and are chosen for contrast between series rather than
    /// against the page.
    let forTheme (theme: IThemeContext) : Template =
        let color token fallback =
            match theme.GetColor token with
            | null
            | "" -> fallback
            | value -> value

        let surface = color "CellOutputBackground" "#FFFFFF"
        let foreground = color "CellOutputForeground" "#1E1E1E"
        let gridline = color "BorderDefault" "#E0E0E0"

        let font = theme.GetFont "UIFont"

        // A theme names one family, and a notebook is read on machines that may not have it: the
        // stock family is a Windows one, which falls back to a serif on macOS and Linux and leaves
        // the chart looking nothing like the text around it. Naming the family first and following
        // it with generic fallbacks keeps the theme's choice where it is installed.
        let fontFamily = $"{font.Family}, system-ui, sans-serif"

        let axisTemplate () =
            LinearAxis.init (
                ShowLine = true,
                ZeroLine = true,
                LineColor = Color.fromString gridline,
                ZeroLineColor = Color.fromString gridline,
                GridColor = Color.fromString gridline,
                TickColor = Color.fromString gridline
            )

        let layoutTemplate =
            Layout.init (
                PaperBGColor = Color.fromString surface,
                PlotBGColor = Color.fromString surface,
                Font =
                    Font.init (
                        Color = Color.fromString foreground,
                        Family = StyleParam.FontFamily.Custom fontFamily,
                        Size = font.SizePx
                    )
            )
            |> Layout.setLinearAxis (StyleParam.SubPlotId.XAxis 1, axisTemplate ())
            |> Layout.setLinearAxis (StyleParam.SubPlotId.YAxis 1, axisTemplate ())

        Template.init layoutTemplate

    /// Restyles the chart from the notebook's own theme values once it is on the page.
    ///
    /// The colors baked in above come from the kernel's view of the theme, which is the right
    /// answer on a served notebook and a guess inside an editor, where the notebook takes its
    /// look from the editor's current theme instead. The page publishes what it actually resolved
    /// as custom properties, and a notebook that does so makes them available to the frame the
    /// chart is drawn in. Reading them there and restyling is what makes a chart match the page it
    /// is on rather than the theme the kernel believed was active.
    ///
    /// Nothing here is required: a page that publishes none of these leaves the baked-in colors
    /// standing, which is why they are chosen to be legible on their own.
    let private restyleScript =
        """
(function () {
    var TOKENS = {
        surface: '--verso-cell-output-background',
        text: '--verso-cell-output-foreground',
        line: '--verso-border-default',
        fontFamily: '--verso-ui-font-family',
        fontSize: '--verso-ui-font-size'
    };

    function read() {
        var style = getComputedStyle(document.documentElement);
        var out = {};
        for (var key in TOKENS) {
            var value = style.getPropertyValue(TOKENS[key]).trim();
            if (value) { out[key] = value; }
        }
        return out;
    }

    function patch(theme) {
        var update = {};
        if (theme.surface) {
            update['paper_bgcolor'] = theme.surface;
            update['plot_bgcolor'] = theme.surface;
        }
        if (theme.text) { update['font.color'] = theme.text; }
        if (theme.fontFamily) { update['font.family'] = theme.fontFamily; }
        if (theme.fontSize) { update['font.size'] = parseFloat(theme.fontSize); }
        if (theme.line) {
            ['xaxis', 'yaxis'].forEach(function (axis) {
                update[axis + '.gridcolor'] = theme.line;
                update[axis + '.linecolor'] = theme.line;
                update[axis + '.zerolinecolor'] = theme.line;
                update[axis + '.tickcolor'] = theme.line;
            });
        }
        return update;
    }

    function apply() {
        var plots = document.querySelectorAll('.js-plotly-plot');
        if (!plots.length || !window.Plotly) { return false; }

        var update = patch(read());
        for (var key in update) {
            for (var i = 0; i < plots.length; i++) {
                try { Plotly.relayout(plots[i], update); } catch (e) { }
            }
            break;
        }
        return true;
    }

    // The chart's own script runs as the document is parsed, but the plot it starts settles a tick
    // later, so this retries rather than assuming the graph is there to restyle.
    if (!apply()) {
        window.addEventListener('load', apply);
        setTimeout(apply, 50);
        setTimeout(apply, 300);
    }

    // Announced by the notebook when its theme changes, so a chart already drawn follows along
    // instead of waiting to be re-run.
    window.addEventListener('verso:themechanged', apply);
})();
"""

    /// A file name for the chart's snapshot, taken from its title so a saved image says what it is.
    ///
    /// Only characters that are unremarkable in a file name on any platform survive; a chart with
    /// no title, or a title that leaves nothing behind, keeps the library default.
    let private snapshotName (layout: Layout) =
        // Read as the base type rather than as a Title: copying a layout rebuilds what is nested
        // inside it as plain dynamic objects, so asking for the concrete type finds nothing.
        let title =
            try
                match layout.TryGetTypedPropertyValue<DynamicObj>("title") with
                | Some title -> title.TryGetTypedPropertyValue<string>("text")
                | None -> None
            with _ ->
                None

        match title with
        | None -> None
        | Some text ->
            let cleaned =
                text.Trim()
                |> Seq.map (fun c -> if Char.IsLetterOrDigit c then Char.ToLowerInvariant c else '-')
                |> Seq.toArray
                |> System.String
            let collapsed =
                cleaned.Split('-', StringSplitOptions.RemoveEmptyEntries)
                |> String.concat "-"
            if collapsed.Length = 0 then None else Some collapsed

    /// Prepares a chart for display inside a notebook cell.
    ///
    /// Three things are adjusted, each only where the chart has not asked for something else. The
    /// template becomes the theme-derived one, so the chart reads as part of the page. The pixel
    /// width and height that every chart is created with are dropped, so the chart fills the width
    /// it is given; charts already carry a responsive config, which is what then sizes them. The
    /// snapshot button is pointed at a file named after the chart and drawn at twice the size, so
    /// a saved image is legible and says what it is rather than arriving as "newplot".
    ///
    /// The layout is copied rather than edited, so displaying a chart never alters the value the
    /// caller is holding.
    let forDisplay (theme: IThemeContext) (chart: GenericChart) : GenericChart =
        let original = GenericChart.getLayout chart
        let layout = Layout()
        original.DeepCopyPropertiesTo layout

        let isStillDefault name value =
            match layout.TryGetTypedPropertyValue<int>(name) with
            | Some actual -> actual = value
            | None -> false

        if isStillDefault "width" Defaults.DefaultWidth
           && isStillDefault "height" Defaults.DefaultHeight then
            layout.RemoveProperty "width" |> ignore
            layout.RemoveProperty "height" |> ignore

        let themed = usesDefaultTemplate layout

        let layout =
            if themed then
                layout |> Layout.style (Template = (forTheme theme :> DynamicObj))
            else
                layout

        let chart =
            chart
            |> GenericChart.setLayout layout
            |> Chart.withConfigStyle (
                ToImageButtonOptions =
                    ToImageButtonOptions.init (
                        Format = StyleParam.ImageFormat.PNG,
                        Scale = 2.,
                        ?Filename = snapshotName layout
                    )
            )

        // Only a chart left to the notebook's theme follows the page. One given a template by its
        // author keeps it.
        if themed then
            chart
            |> Chart.withDisplayOptionsStyle (ChartDescription = [ script [] [ rawText restyleScript ] ])
        else
            chart
