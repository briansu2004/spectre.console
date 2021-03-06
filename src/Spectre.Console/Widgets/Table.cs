using System;
using System.Collections.Generic;
using System.Linq;
using Spectre.Console.Internal;
using Spectre.Console.Rendering;

namespace Spectre.Console
{
    /// <summary>
    /// A renderable table.
    /// </summary>
    public sealed class Table : Renderable, IHasTableBorder, IExpandable, IAlignable
    {
        private const int EdgeCount = 2;

        private readonly List<TableColumn> _columns;
        private readonly List<TableRow> _rows;

        private static Style _defaultHeadingStyle = new Style(Color.Silver);
        private static Style _defaultCaptionStyle = new Style(Color.Grey);

        /// <summary>
        /// Gets the table columns.
        /// </summary>
        public IReadOnlyList<TableColumn> Columns => _columns;

        /// <summary>
        /// Gets the table rows.
        /// </summary>
        public IReadOnlyList<TableRow> Rows => _rows;

        /// <inheritdoc/>
        public TableBorder Border { get; set; } = TableBorder.Square;

        /// <inheritdoc/>
        public Style? BorderStyle { get; set; }

        /// <inheritdoc/>
        public bool UseSafeBorder { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether or not table headers should be shown.
        /// </summary>
        public bool ShowHeaders { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether or not table footers should be shown.
        /// </summary>
        public bool ShowFooters { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether or not the table should
        /// fit the available space. If <c>false</c>, the table width will be
        /// auto calculated. Defaults to <c>false</c>.
        /// </summary>
        public bool Expand { get; set; }

        /// <summary>
        /// Gets or sets the width of the table.
        /// </summary>
        public int? Width { get; set; }

        /// <summary>
        /// Gets or sets the table title.
        /// </summary>
        public TableTitle? Title { get; set; }

        /// <summary>
        /// Gets or sets the table footnote.
        /// </summary>
        public TableTitle? Caption { get; set; }

        /// <inheritdoc/>
        public Justify? Alignment { get; set; }

        // Whether this is a grid or not.
        internal bool IsGrid { get; set; }

        // Whether or not the most right cell should be padded.
        // This is almost always the case, unless we're rendering
        // a grid without explicit padding in the last cell.
        internal bool PadRightCell { get; set; } = true;

        /// <summary>
        /// Initializes a new instance of the <see cref="Table"/> class.
        /// </summary>
        public Table()
        {
            _columns = new List<TableColumn>();
            _rows = new List<TableRow>();
        }

        /// <summary>
        /// Adds a column to the table.
        /// </summary>
        /// <param name="column">The column to add.</param>
        /// <returns>The same instance so that multiple calls can be chained.</returns>
        public Table AddColumn(TableColumn column)
        {
            if (column is null)
            {
                throw new ArgumentNullException(nameof(column));
            }

            if (_rows.Count > 0)
            {
                throw new InvalidOperationException("Cannot add new columns to table with existing rows.");
            }

            _columns.Add(column);
            return this;
        }

        /// <summary>
        /// Adds a row to the table.
        /// </summary>
        /// <param name="columns">The row columns to add.</param>
        /// <returns>The same instance so that multiple calls can be chained.</returns>
        public Table AddRow(IEnumerable<IRenderable> columns)
        {
            if (columns is null)
            {
                throw new ArgumentNullException(nameof(columns));
            }

            var rowColumnCount = columns.GetCount();
            if (rowColumnCount > _columns.Count)
            {
                throw new InvalidOperationException("The number of row columns are greater than the number of table columns.");
            }

            _rows.Add(new TableRow(columns));

            // Need to add missing columns?
            if (rowColumnCount < _columns.Count)
            {
                var diff = _columns.Count - rowColumnCount;
                Enumerable.Range(0, diff).ForEach(_ => _rows.Last().Add(Text.Empty));
            }

            return this;
        }

        /// <inheritdoc/>
        protected override Measurement Measure(RenderContext context, int maxWidth)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (Width != null)
            {
                maxWidth = Math.Min(Width.Value, maxWidth);
            }

            maxWidth -= GetExtraWidth(includePadding: true);

            var measurements = _columns.Select(column => MeasureColumn(column, context, maxWidth)).ToList();
            var min = measurements.Sum(x => x.Min) + GetExtraWidth(includePadding: true);
            var max = Width ?? measurements.Sum(x => x.Max) + GetExtraWidth(includePadding: true);

            return new Measurement(min, max);
        }

        /// <inheritdoc/>
        protected override IEnumerable<Segment> Render(RenderContext context, int maxWidth)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var border = Border.GetSafeBorder((context.LegacyConsole || !context.Unicode) && UseSafeBorder);
            var borderStyle = BorderStyle ?? Style.Plain;

            var tableWidth = maxWidth;
            var actualMaxWidth = maxWidth;

            var showBorder = Border.Visible;
            var hideBorder = !Border.Visible;
            var hasRows = _rows.Count > 0;
            var hasFooters = _columns.Any(c => c.Footer != null);

            if (Width != null)
            {
                maxWidth = Math.Min(Width.Value, maxWidth);
            }

            maxWidth -= GetExtraWidth(includePadding: true);

            // Calculate the column and table widths
            var columnWidths = CalculateColumnWidths(context, maxWidth);

            // Update the table width.
            tableWidth = columnWidths.Sum() + GetExtraWidth(includePadding: true);
            if (tableWidth <= 0 || tableWidth > actualMaxWidth || columnWidths.Any(c => c <= 0))
            {
                return new List<Segment>(new[] { new Segment("…", BorderStyle ?? Style.Plain) });
            }

            var rows = new List<TableRow>();
            if (ShowHeaders)
            {
                // Add columns to top of rows
                rows.Add(new TableRow(new List<IRenderable>(_columns.Select(c => c.Header))));
            }

            // Add rows.
            rows.AddRange(_rows);

            if (hasFooters)
            {
                rows.Add(new TableRow(new List<IRenderable>(_columns.Select(c => c.Footer ?? Text.Empty))));
            }

            var result = new List<Segment>();
            result.AddRange(RenderAnnotation(context, Title, actualMaxWidth, tableWidth, _defaultHeadingStyle));

            // Iterate all rows
            foreach (var (index, firstRow, lastRow, row) in rows.Enumerate())
            {
                var cellHeight = 1;

                // Get the list of cells for the row and calculate the cell height
                var cells = new List<List<SegmentLine>>();
                foreach (var (columnIndex, _, _, (rowWidth, cell)) in columnWidths.Zip(row).Enumerate())
                {
                    var justification = _columns[columnIndex].Alignment;
                    var childContext = context.WithJustification(justification);

                    var lines = Segment.SplitLines(context, cell.Render(childContext, rowWidth));
                    cellHeight = Math.Max(cellHeight, lines.Count);
                    cells.Add(lines);
                }

                // Show top of header?
                if (firstRow && showBorder)
                {
                    var separator = Aligner.Align(context, border.GetColumnRow(TablePart.Top, columnWidths, _columns), Alignment, actualMaxWidth);
                    result.Add(new Segment(separator, borderStyle));
                    result.Add(Segment.LineBreak);
                }

                // Show footer separator?
                if (ShowFooters && lastRow && showBorder && hasFooters)
                {
                    var textBorder = border.GetColumnRow(TablePart.FooterSeparator, columnWidths, _columns);
                    if (!string.IsNullOrEmpty(textBorder))
                    {
                        var separator = Aligner.Align(context, textBorder, Alignment, actualMaxWidth);
                        result.Add(new Segment(separator, borderStyle));
                        result.Add(Segment.LineBreak);
                    }
                }

                // Make cells the same shape
                cells = Segment.MakeSameHeight(cellHeight, cells);

                // Iterate through each cell row
                foreach (var cellRowIndex in Enumerable.Range(0, cellHeight))
                {
                    var rowResult = new List<Segment>();

                    foreach (var (cellIndex, firstCell, lastCell, cell) in cells.Enumerate())
                    {
                        if (firstCell && showBorder)
                        {
                            // Show left column edge
                            var part = firstRow && ShowHeaders ? TableBorderPart.HeaderLeft : TableBorderPart.CellLeft;
                            rowResult.Add(new Segment(border.GetPart(part), borderStyle));
                        }

                        // Pad column on left side.
                        if (showBorder || IsGrid)
                        {
                            var leftPadding = _columns[cellIndex].Padding.GetLeftSafe();
                            if (leftPadding > 0)
                            {
                                rowResult.Add(new Segment(new string(' ', leftPadding)));
                            }
                        }

                        // Add content
                        rowResult.AddRange(cell[cellRowIndex]);

                        // Pad cell content right
                        var length = cell[cellRowIndex].Sum(segment => segment.CellCount(context));
                        if (length < columnWidths[cellIndex])
                        {
                            rowResult.Add(new Segment(new string(' ', columnWidths[cellIndex] - length)));
                        }

                        // Pad column on the right side
                        if (showBorder || (hideBorder && !lastCell) || (hideBorder && lastCell && IsGrid && PadRightCell))
                        {
                            var rightPadding = _columns[cellIndex].Padding.GetRightSafe();
                            if (rightPadding > 0)
                            {
                                rowResult.Add(new Segment(new string(' ', rightPadding)));
                            }
                        }

                        if (lastCell && showBorder)
                        {
                            // Add right column edge
                            var part = firstRow && ShowHeaders ? TableBorderPart.HeaderRight : TableBorderPart.CellRight;
                            rowResult.Add(new Segment(border.GetPart(part), borderStyle));
                        }
                        else if (showBorder)
                        {
                            // Add column separator
                            var part = firstRow && ShowHeaders ? TableBorderPart.HeaderSeparator : TableBorderPart.CellSeparator;
                            rowResult.Add(new Segment(border.GetPart(part), borderStyle));
                        }
                    }

                    // Align the row result.
                    Aligner.Align(context, rowResult, Alignment, actualMaxWidth);

                    // Is the row larger than the allowed max width?
                    if (Segment.CellCount(context, rowResult) > actualMaxWidth)
                    {
                        result.AddRange(Segment.Truncate(context, rowResult, actualMaxWidth));
                    }
                    else
                    {
                        result.AddRange(rowResult);
                    }

                    result.Add(Segment.LineBreak);
                }

                // Show header separator?
                if (firstRow && showBorder && ShowHeaders && hasRows)
                {
                    var separator = Aligner.Align(context, border.GetColumnRow(TablePart.HeaderSeparator, columnWidths, _columns), Alignment, actualMaxWidth);
                    result.Add(new Segment(separator, borderStyle));
                    result.Add(Segment.LineBreak);
                }

                // Show bottom of footer?
                if (lastRow && showBorder)
                {
                    var separator = Aligner.Align(context, border.GetColumnRow(TablePart.Bottom, columnWidths, _columns), Alignment, actualMaxWidth);
                    result.Add(new Segment(separator, borderStyle));
                    result.Add(Segment.LineBreak);
                }
            }

            result.AddRange(RenderAnnotation(context, Caption, actualMaxWidth, tableWidth, _defaultCaptionStyle));

            return result;
        }

        // Calculate the widths of each column, including padding, not including borders.
        // Ported from Rich by Will McGugan, licensed under MIT.
        // https://github.com/willmcgugan/rich/blob/527475837ebbfc427530b3ee0d4d0741d2d0fc6d/rich/table.py#L394
        private List<int> CalculateColumnWidths(RenderContext options, int maxWidth)
        {
            var width_ranges = _columns.Select(column => MeasureColumn(column, options, maxWidth)).ToArray();
            var widths = width_ranges.Select(range => range.Max).ToList();

            var tableWidth = widths.Sum();
            if (tableWidth > maxWidth)
            {
                var wrappable = _columns.Select(c => !c.NoWrap).ToList();
                widths = CollapseWidths(widths, wrappable, maxWidth);
                tableWidth = widths.Sum();

                // last resort, reduce columns evenly
                if (tableWidth > maxWidth)
                {
                    var excessWidth = tableWidth - maxWidth;
                    widths = Ratio.Reduce(excessWidth, widths.Select(_ => 1).ToList(), widths, widths);
                    tableWidth = widths.Sum();
                }
            }

            if (tableWidth < maxWidth && ShouldExpand())
            {
                var padWidths = Ratio.Distribute(maxWidth - tableWidth, widths);
                widths = widths.Zip(padWidths, (a, b) => (a, b)).Select(f => f.a + f.b).ToList();
            }

            return widths;
        }

        // Reduce widths so that the total is less or equal to the max width.
        // Ported from Rich by Will McGugan, licensed under MIT.
        // https://github.com/willmcgugan/rich/blob/527475837ebbfc427530b3ee0d4d0741d2d0fc6d/rich/table.py#L442
        private static List<int> CollapseWidths(List<int> widths, List<bool> wrappable, int maxWidth)
        {
            var totalWidth = widths.Sum();
            var excessWidth = totalWidth - maxWidth;

            if (wrappable.AnyTrue())
            {
                while (totalWidth != 0 && excessWidth > 0)
                {
                    var maxColumn = widths.Zip(wrappable, (first, second) => (width: first, allowWrap: second))
                        .Where(x => x.allowWrap)
                        .Max(x => x.width);

                    var secondMaxColumn = widths.Zip(wrappable, (width, allowWrap) => allowWrap && width != maxColumn ? width : 1).Max();
                    var columnDifference = maxColumn - secondMaxColumn;

                    var ratios = widths.Zip(wrappable, (width, allowWrap) => width == maxColumn && allowWrap ? 1 : 0).ToList();
                    if (!ratios.Any(x => x != 0) || columnDifference == 0)
                    {
                        break;
                    }

                    var maxReduce = widths.Select(_ => Math.Min(excessWidth, columnDifference)).ToList();
                    widths = Ratio.Reduce(excessWidth, ratios, maxReduce, widths);

                    totalWidth = widths.Sum();
                    excessWidth = totalWidth - maxWidth;
                }
            }

            return widths;
        }

        private IEnumerable<Segment> RenderAnnotation(
            RenderContext context, TableTitle? header,
            int maxWidth, int tableWidth, Style defaultStyle)
        {
            if (header == null)
            {
                return Array.Empty<Segment>();
            }

            var paragraph = new Markup(header.Text.Capitalize(), header.Style ?? defaultStyle)
                .Alignment(Justify.Center)
                .Overflow(Overflow.Ellipsis);

            var items = new List<Segment>();
            items.AddRange(((IRenderable)paragraph).Render(context, tableWidth));

            // Align over the whole buffer area
            Aligner.Align(context, items, Alignment, maxWidth);

            items.Add(Segment.LineBreak);
            return items;
        }

        private (int Min, int Max) MeasureColumn(TableColumn column, RenderContext options, int maxWidth)
        {
            var padding = column.Padding?.GetWidth() ?? 0;

            // Predetermined width?
            if (column.Width != null)
            {
                return (column.Width.Value + padding, column.Width.Value + padding);
            }

            var columnIndex = _columns.IndexOf(column);
            var rows = _rows.Select(row => row[columnIndex]);

            var minWidths = new List<int>();
            var maxWidths = new List<int>();

            // Include columns (both header and footer) in measurement
            var headerMeasure = column.Header.Measure(options, maxWidth);
            var footerMeasure = column.Footer?.Measure(options, maxWidth) ?? headerMeasure;
            minWidths.Add(Math.Min(headerMeasure.Min, footerMeasure.Min));
            maxWidths.Add(Math.Max(headerMeasure.Max, footerMeasure.Max));

            foreach (var row in rows)
            {
                var rowMeasure = row.Measure(options, maxWidth);
                minWidths.Add(rowMeasure.Min);
                maxWidths.Add(rowMeasure.Max);
            }

            return (minWidths.Count > 0 ? minWidths.Max() : padding,
                    maxWidths.Count > 0 ? maxWidths.Max() : maxWidth);
        }

        private int GetExtraWidth(bool includePadding)
        {
            var hideBorder = !Border.Visible;
            var separators = hideBorder ? 0 : _columns.Count - 1;
            var edges = hideBorder ? 0 : EdgeCount;
            var padding = includePadding ? _columns.Select(x => x.Padding?.GetWidth() ?? 0).Sum() : 0;

            if (!PadRightCell)
            {
                padding -= _columns.Last().Padding.GetRightSafe();
            }

            return separators + edges + padding;
        }

        private bool ShouldExpand()
        {
            return Expand || Width != null;
        }
    }
}
