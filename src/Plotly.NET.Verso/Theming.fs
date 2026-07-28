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

    /// Styles the chart from the notebook's own theme values.
    ///
    /// The colors baked in above come from the kernel's view of the theme, which is the right
    /// answer on a served notebook and a guess inside an editor, where the notebook takes its
    /// look from the editor's current theme instead. The page publishes what it actually resolved
    /// as custom properties, and a notebook that does so makes them available to the frame the
    /// chart is drawn in. Reading them there is what makes a chart match the page it is on rather
    /// than the theme the kernel believed was active.
    ///
    /// This runs in the document's head, ahead of the chart's own script, and reaches the chart by
    /// intercepting the call that draws it. Restyling afterwards is the obvious way to write this
    /// and it flashes: the chart is drawn once in the kernel's colors, the browser paints it, and
    /// the corrected colors arrive a frame or two later, which in an editor on a dark theme means a
    /// white chart appears and then turns dark. Nothing is corrected here, because nothing wrong is
    /// ever drawn.
    ///
    /// Nothing here is required: a page that publishes none of these leaves the baked-in colors
    /// standing, which is why they are chosen to be legible on their own.
    let private themeScript =
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

    // Written as the flat paths plotly accepts for an update, which is also what the theme-change
    // path below hands to relayout.
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

    function isEmpty(update) {
        for (var key in update) { return false; }
        return true;
    }

    // Walks a flat path into the layout the chart is about to be drawn with. Written at the layout
    // itself rather than into its template, so it wins over the template either way.
    function assign(layout, update) {
        for (var key in update) {
            var parts = key.split('.');
            var target = layout;
            for (var i = 0; i < parts.length - 1; i++) {
                var name = parts[i];
                if (typeof target[name] !== 'object' || target[name] === null) { target[name] = {}; }
                target = target[name];
            }
            target[parts[parts.length - 1]] = update[key];
        }
        return layout;
    }

    function themed(layout) {
        var update = patch(read());
        if (isEmpty(update)) { return layout; }
        return assign(layout || {}, update);
    }

    // Takes the layout on its way to being drawn. The single-argument form some callers use carries
    // the layout inside the first object instead, so both are handled.
    function intercept(name) {
        var original = window.Plotly[name];
        if (typeof original !== 'function') { return; }

        window.Plotly[name] = function (div, data, layout, config) {
            try {
                if (data && !Array.isArray(data) && typeof data === 'object' && 'layout' in data) {
                    data.layout = themed(data.layout);
                } else {
                    layout = themed(layout);
                }
            } catch (e) { }
            return original.call(this, div, data, layout, config);
        };
    }

    // Content already drawn in colors it read once cannot be reached any other way, so a later
    // theme change is a restyle. This is also the whole story for a chart drawn before this ran,
    // which is what happens when plotly is not on the page yet at this point.
    function restyle() {
        var plots = document.querySelectorAll('.js-plotly-plot');
        if (!plots.length || !window.Plotly) { return false; }

        var update = patch(read());
        if (isEmpty(update)) { return true; }

        for (var i = 0; i < plots.length; i++) {
            try { Plotly.relayout(plots[i], update); } catch (e) { }
        }
        return true;
    }

    if (window.Plotly) {
        intercept('newPlot');
        intercept('react');
    } else {
        // No plotly to intercept: it is fetched by the chart's own script (a require reference) or
        // left to the page. Fall back to restyling once the graph exists.
        if (!restyle()) {
            window.addEventListener('load', restyle);
            setTimeout(restyle, 50);
            setTimeout(restyle, 300);
        }
    }

    // Announced by the notebook when its theme changes, so a chart already drawn follows along
    // instead of waiting to be re-run.
    window.addEventListener('verso:themechanged', restyle);
})();
"""

    /// Keeps the chart the width of the cell it sits in, and only the width.
    ///
    /// A chart cannot be sized by the frame it is drawn in, because the frame is sized by the chart:
    /// the frame starts at a provisional height, the document reports how tall it turned out, and the
    /// frame is set to match. That leaves the frame's height changing once as a matter of course,
    /// immediately after the first draw, and plotly's own responsive handling redraws on any viewport
    /// change whether or not the size it computes has changed. The redraw clears the plot while it
    /// runs, so the chart appears, empties, and appears again.
    ///
    /// So plotly's handling is turned off and replaced with this, which reacts to the one dimension
    /// that is not part of that loop. Widening or narrowing the cell still refits the chart; the
    /// frame finding its own height no longer touches it.
    let private sizingScript =
        """
(function () {
    var fittedTo = null;

    // The room the chart has, rather than the width it was last drawn at: plotly writes its own
    // width onto the element, so asking the chart would only ever report what it already is.
    function available() {
        var body = document.body;
        return (body && body.clientWidth) || document.documentElement.clientWidth || 0;
    }

    function follow() {
        var plot = document.querySelector('.js-plotly-plot');
        if (!plot || !window.Plotly) { return; }

        var width = available();
        if (!width || width === fittedTo) { return; }
        fittedTo = width;

        try { Plotly.relayout(plot, { width: width }); } catch (e) { }
    }

    function begin() {
        // Whatever the chart was drawn to fill, so the first change is measured against it.
        fittedTo = available();
        window.addEventListener('resize', follow);
        if (window.ResizeObserver) {
            try { new ResizeObserver(follow).observe(document.documentElement); } catch (e) { }
        }
    }

    if (document.readyState === 'loading') {
        window.addEventListener('load', begin);
    } else {
        begin();
    }
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
    /// it is given. The snapshot button is pointed at a file named after the chart and drawn at twice
    /// the size, so a saved image is legible and says what it is rather than arriving as "newplot".
    ///
    /// Sizing is then taken over from plotly, whose own responsive handling redraws the chart every
    /// time the frame it sits in changes size, including the once that frame is bound to change as it
    /// takes the height the chart just reported. See <c>sizingScript</c>.
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
                Responsive = false,
                ToImageButtonOptions =
                    ToImageButtonOptions.init (
                        Format = StyleParam.ImageFormat.PNG,
                        Scale = 2.,
                        ?Filename = snapshotName layout
                    )
            )

        // In the head rather than after the chart, because the document's head is the only place a
        // script runs before the chart is drawn. Plotly's own reference is written ahead of these
        // tags, so it is loaded and ready to intercept by the time these run.
        //
        // Sizing goes in either way. The theme only goes in for a chart left to the notebook's theme:
        // one given a template by its author keeps it.
        let headTags =
            [
                if themed then
                    script [] [ rawText themeScript ]
                script [] [ rawText sizingScript ]
            ]

        chart |> Chart.withDisplayOptionsStyle (AdditionalHeadTags = headTags)
