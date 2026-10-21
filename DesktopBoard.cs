using System.Drawing;
using System.Drawing.Drawing2D;

namespace DesktopSessionManager;

// Desktop ordering rules used by the board; kept free of UI so they can be tested.
internal static class DesktopOrder
{
    // Pinned and all-desktop windows have no desktop of their own to move.
    public static bool Movable(SavedWindow w) => !w.AllDesktops && !w.Pinned;
    // Desktops shown on the board: every one up to the highest a window uses, gaps included.
    public static int Count(IEnumerable<SavedWindow> windows) => windows.Where(Movable).Select(w => w.DesktopIndex + 1).DefaultIfEmpty(0).Max();
    public static string NameOf(IEnumerable<SavedWindow> windows, int desktop) =>
        windows.Where(w => Movable(w) && w.DesktopIndex == desktop).Select(w => w.DesktopName).FirstOrDefault(n => n != "") ?? "";
    // Moves desktop `from` to position `to`; its windows go with it and the desktops in between shift over.
    public static void Reorder(IEnumerable<SavedWindow> windows, int from, int to)
    {
        if (from == to) return;
        foreach (var w in windows.Where(w => !w.AllDesktops))
        {
            int i = w.DesktopIndex;
            if (i == from) w.DesktopIndex = to;
            else if (from < to && i > from && i <= to) w.DesktopIndex = i - 1;
            else if (from > to && i >= to && i < from) w.DesktopIndex = i + 1;
        }
    }
    // Moves a window to another monitor, keeping its offset within the monitor; restore clamps what no longer fits.
    public static void MoveToMonitor(SavedWindow w, Rectangle from, Rectangle to) { w.Left += to.Left - from.Left; w.Top += to.Top - from.Top; }
    public static void MoveTo(IEnumerable<SavedWindow> targets, IEnumerable<SavedWindow> all, int desktop)
    {
        string name = NameOf(all, desktop);
        foreach (var w in targets.Where(Movable).ToList()) { w.DesktopIndex = desktop; w.DesktopName = name; }
    }
}

// Task View style picture of a profile: one card per virtual desktop with a miniature of its windows.
// Dragging a card reorders desktops; dropping a window on a card's screen puts it on that desktop and monitor.
// Windows dragged in from the open-window list are dropped the same way.
internal sealed class DesktopBoard : Panel
{
    private const int CardWidth = 220, Gap = 14, HeaderHeight = 30, RowHeight = 22, Pad = 8, AllDesktopsSlot = -1;
    private static readonly Color[] Palette =
    [
        Color.FromArgb(76, 139, 245), Color.FromArgb(46, 184, 138), Color.FromArgb(242, 158, 56), Color.FromArgb(217, 91, 140),
        Color.FromArgb(142, 108, 239), Color.FromArgb(63, 181, 209), Color.FromArgb(201, 123, 74), Color.FromArgb(124, 179, 66)
    ];
    private sealed class Card
    {
        public int Slot;
        public Rectangle Bounds, Header, Map;
        public readonly List<(SavedWindow Window, Rectangle Bounds)> Rows = [], Shapes = [];
    }
    private enum DragKind { None, Window, Desktop }

    private List<SavedWindow> windows = [];
    private readonly List<Card> cards = [];
    private readonly HashSet<string> selected = [];
    private readonly ToolTip tip = new();
    private readonly ContextMenuStrip menu = new();
    private Rectangle screen = new(0, 0, 1920, 1080);
    private Rectangle[] monitors = [];
    private int count;
    private DragKind pending, dragging;
    private Point downAt, mouseAt;
    private bool downOnSelected;
    private SavedWindow? dragWindow, hovered;
    private int dragSlot = -1, dropSlot = -1, dropGap = -1;
    private Rectangle? dropMonitor;
    private SavedWindow[]? incoming; // windows being dragged in from outside the board
    private Font? bold, small;
    private readonly Dictionary<string, int> colors = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;
    public event Action<SavedWindow>? EditRequested;
    public event Action? RemoveRequested;
    // Windows dropped from outside; the owner adds copies and applies the placement to each.
    public event Action<SavedWindow[], Action<SavedWindow>>? Dropped;
    public Func<SavedWindow, string> IssueOf { get; set; } = _ => "";
    public List<SavedWindow> Selected => windows.Where(w => selected.Contains(w.Id)).ToList();

    public DesktopBoard()
    {
        AutoScroll = true; TabStop = true; AllowDrop = true; BackColor = SystemColors.Control;
        SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { tip.Dispose(); menu.Dispose(); bold?.Dispose(); small?.Dispose(); }
        base.Dispose(disposing);
    }
    protected override void OnFontChanged(EventArgs e) { bold?.Dispose(); small?.Dispose(); bold = small = null; base.OnFontChanged(e); }
    private Font Bold => bold ??= new Font(Font, FontStyle.Bold);
    private Font Small => small ??= new Font(Font.FontFamily, Math.Max(6.5f, Font.Size - 1.5f));

    public void SetWindows(List<SavedWindow> source)
    {
        windows = source;
        selected.IntersectWith(source.Select(w => w.Id));
        LayoutCards(); Invalidate();
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutCards(); }

    private void LayoutCards()
    {
        cards.Clear();
        monitors = Screen.AllScreens.Select(s => s.Bounds).ToArray();
        screen = monitors.Aggregate(Rectangle.Union);
        count = DesktopOrder.Count(windows);
        colors.Clear();
        foreach (var w in windows) colors.TryAdd(AppName(w), colors.Count % Palette.Length);
        int mapWidth = CardWidth - 2 * Pad;
        int mapHeight = Math.Clamp(mapWidth * screen.Height / Math.Max(1, screen.Width), 50, 150);
        var slots = new List<int>();
        if (windows.Any(w => !DesktopOrder.Movable(w))) slots.Add(AllDesktopsSlot);
        slots.AddRange(Enumerable.Range(0, count));
        slots.Add(count); // drop target that creates the next desktop
        int x = Gap, y = Gap, bottom = Gap;
        foreach (int slot in slots)
        {
            var members = windows.Where(w => slot == AllDesktopsSlot ? !DesktopOrder.Movable(w) : DesktopOrder.Movable(w) && w.DesktopIndex == slot).ToList();
            int height = HeaderHeight + mapHeight + 2 * Pad + (slot == count ? 0 : Math.Max(1, members.Count) * RowHeight + Pad);
            if (x > Gap && x + CardWidth + Gap > ClientSize.Width) { x = Gap; y = bottom + Gap; }
            var card = new Card
            {
                Slot = slot, Bounds = new Rectangle(x, y, CardWidth, height), Header = new Rectangle(x, y, CardWidth, HeaderHeight),
                Map = new Rectangle(x + Pad, y + HeaderHeight, mapWidth, mapHeight)
            };
            int rowY = card.Map.Bottom + Pad;
            foreach (var w in members) { card.Rows.Add((w, new Rectangle(x + Pad / 2, rowY, CardWidth - Pad, RowHeight))); rowY += RowHeight; }
            // Larger windows first so small ones stay visible and clickable on top.
            foreach (var w in members.Where(w => !w.Minimized).OrderByDescending(w => (long)w.Width * w.Height))
                card.Shapes.Add((w, ToMap(Placement(w), card.Map)));
            cards.Add(card);
            x += CardWidth + Gap; bottom = Math.Max(bottom, card.Bounds.Bottom);
        }
        AutoScrollMinSize = new Size(0, bottom + Gap);
    }
    // Where the window will actually land: restore clamps it to a screen, and maximized fills that screen.
    private static Rectangle Placement(SavedWindow w)
    {
        var rect = MainForm.ClampToScreens(w);
        return w.Maximized ? Screen.FromRectangle(rect).WorkingArea : rect;
    }
    private Rectangle ToMap(Rectangle r, Rectangle map)
    {
        float sx = map.Width / (float)Math.Max(1, screen.Width), sy = map.Height / (float)Math.Max(1, screen.Height);
        return Rectangle.FromLTRB(map.Left + (int)((r.Left - screen.Left) * sx), map.Top + (int)((r.Top - screen.Top) * sy),
            map.Left + (int)Math.Ceiling((r.Right - screen.Left) * sx), map.Top + (int)Math.Ceiling((r.Bottom - screen.Top) * sy));
    }
    private Rectangle Screen2Client(Rectangle r) { r.Offset(AutoScrollPosition); return r; }
    private Point Client2Layout(Point p) => new(p.X - AutoScrollPosition.X, p.Y - AutoScrollPosition.Y);
    private static string AppName(SavedWindow w) => Path.GetFileNameWithoutExtension(w.Path);
    // Each app keeps one colour across all cards; apps get colours in order of first appearance.
    private Color ColorOf(SavedWindow w) => Palette[colors.TryGetValue(AppName(w), out int i) ? i : 0];

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (windows.Count == 0)
        {
            TextRenderer.DrawText(g, "왼쪽 창 목록에서 창을 끌어다 놓으면\n데스크톱별 화면으로 여기에 표시됩니다.", Font, ClientRectangle, SystemColors.GrayText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        foreach (var card in cards) DrawCard(g, card);
        if (dragging == DragKind.Desktop && dropGap >= 0) DrawInsertBar(g);
        if (dragging != DragKind.None) DrawGhost(g);
    }
    private void DrawCard(Graphics g, Card c)
    {
        bool isNew = c.Slot == count, isAll = c.Slot == AllDesktopsSlot;
        bool target = dragging == DragKind.Window && dropSlot == c.Slot;
        bool lifted = dragging == DragKind.Desktop && dragSlot == c.Slot;
        var bounds = Screen2Client(c.Bounds);
        using (var path = Rounded(bounds, 8))
        {
            if (!isNew || target)
                using (var fill = new SolidBrush(target ? SystemColors.Info : lifted ? SystemColors.ControlLight : SystemColors.Window)) g.FillPath(fill, path);
            using var pen = new Pen(target ? SystemColors.Highlight : SystemColors.ControlDark, target ? 2 : 1) { DashStyle = isNew ? DashStyle.Dash : DashStyle.Solid };
            g.DrawPath(pen, path);
        }
        var header = Screen2Client(c.Header);
        header.Inflate(-Pad, 0);
        var muted = lifted ? SystemColors.GrayText : SystemColors.ControlText;
        const TextFormatFlags line = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        if (isNew)
        {
            TextRenderer.DrawText(g, "+ 새 데스크톱", Bold, header, SystemColors.GrayText, line);
            if (target) { DrawMap(g, c); DrawPreview(g, c); }
            else TextRenderer.DrawText(g, "창을 여기 화면에 끌어 놓으면\n새 데스크톱에 배치됩니다", Font, Screen2Client(c.Map), SystemColors.GrayText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        string countText = $"창 {c.Rows.Count}개";
        int countWidth = TextRenderer.MeasureText(countText, Font).Width;
        TextRenderer.DrawText(g, countText, Font, header, SystemColors.GrayText, line | TextFormatFlags.Right);
        var titleArea = header with { Width = header.Width - countWidth - 4 };
        string title = isAll ? "모든 데스크톱 · 고정" : "≡ 데스크톱 " + (c.Slot + 1);
        TextRenderer.DrawText(g, title, Bold, titleArea, muted, line);
        string name = isAll ? "" : DesktopOrder.NameOf(windows, c.Slot);
        if (name != "")
        {
            int used = TextRenderer.MeasureText(title, Bold).Width;
            TextRenderer.DrawText(g, name, Font, titleArea with { X = titleArea.X + used, Width = titleArea.Width - used }, SystemColors.GrayText, line);
        }
        DrawMap(g, c);
        if (target) DrawPreview(g, c);
        if (c.Rows.Count == 0)
        {
            var empty = Screen2Client(new Rectangle(c.Map.Left, c.Map.Bottom + Pad, c.Map.Width, RowHeight));
            TextRenderer.DrawText(g, "비어 있음", Font, empty, SystemColors.GrayText, line);
        }
        foreach (var (w, r) in c.Rows) DrawRow(g, w, Screen2Client(r));
    }
    private void DrawMap(Graphics g, Card c)
    {
        var map = Screen2Client(c.Map);
        using (var back = new SolidBrush(Color.FromArgb(46, 52, 64))) g.FillRectangle(back, map);
        using (var monitor = new SolidBrush(Color.FromArgb(70, 78, 94)))
            foreach (var m in monitors) { var r = Screen2Client(ToMap(m, c.Map)); r.Inflate(-1, -1); g.FillRectangle(monitor, r); }
        var clip = g.Clip; g.SetClip(map);
        foreach (var (w, mapped) in c.Shapes)
        {
            var r = Screen2Client(mapped);
            bool isSelected = selected.Contains(w.Id);
            using (var fill = new SolidBrush(Color.FromArgb(isSelected ? 235 : 190, ColorOf(w)))) g.FillRectangle(fill, r);
            using (var pen = new Pen(isSelected ? Color.White : Color.FromArgb(120, Color.Black), isSelected ? 2 : 1)) g.DrawRectangle(pen, r);
            if (r.Width > 28 && r.Height > 12)
                TextRenderer.DrawText(g, AppName(w), Small, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
        g.Clip = clip;
    }
    private void DrawRow(Graphics g, SavedWindow w, Rectangle r)
    {
        bool isSelected = selected.Contains(w.Id);
        bool moving = dragging == DragKind.Window && (isSelected ? selected.Contains(dragWindow?.Id ?? "") : w == dragWindow);
        if (isSelected) g.FillRectangle(SystemBrushes.Highlight, r);
        using (var swatch = new SolidBrush(ColorOf(w))) g.FillRectangle(swatch, r.Left + 4, r.Top + (r.Height - 10) / 2, 10, 10);
        string issue = IssueOf(w);
        string text = (issue != "" ? "⚠ " : "") + AppName(w) + " — " + w.Title + (w.Minimized ? " · 최소화" : "");
        var color = isSelected ? SystemColors.HighlightText : moving ? SystemColors.GrayText : issue != "" ? Color.Firebrick : SystemColors.ControlText;
        TextRenderer.DrawText(g, text, Font, r with { X = r.X + 18, Width = r.Width - 20 }, color, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }
    private void DrawInsertBar(Graphics g)
    {
        var desks = cards.Where(c => c.Slot >= 0 && c.Slot < count).ToList();
        var r = Screen2Client(dropGap < desks.Count ? desks[dropGap].Bounds : desks[^1].Bounds);
        int x = dropGap < desks.Count ? r.Left - Gap / 2 : r.Right + Gap / 2;
        using var pen = new Pen(SystemColors.Highlight, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, x, r.Top + 4, x, r.Bottom - 4);
    }
    // Outlines where the dragged windows would land on this card's screen, and the monitor under the cursor.
    private void DrawPreview(Graphics g, Card c)
    {
        var map = Screen2Client(c.Map);
        var clip = g.Clip; g.SetClip(map);
        if (dropMonitor is { } monitor)
            using (var glow = new SolidBrush(Color.FromArgb(60, SystemColors.Highlight))) g.FillRectangle(glow, Screen2Client(ToMap(monitor, c.Map)));
        foreach (var w in DraggedWindows())
        {
            var shown = Screen2Client(ToMap(Landing(w), c.Map));
            using (var fill = new SolidBrush(Color.FromArgb(120, ColorOf(w)))) g.FillRectangle(fill, shown);
            using (var pen = new Pen(Color.White, 2) { DashStyle = DashStyle.Dash }) g.DrawRectangle(pen, shown);
        }
        g.Clip = clip;
        using var border = new Pen(SystemColors.Highlight, 2);
        g.DrawRectangle(border, map);
    }
    private Rectangle Landing(SavedWindow w)
    {
        var r = Placement(w);
        if (dropMonitor is { } to) { var from = MonitorOf(w); r.Offset(to.Left - from.Left, to.Top - from.Top); }
        return r;
    }
    private static Rectangle MonitorOf(SavedWindow w) => Screen.FromRectangle(MainForm.ClampToScreens(w)).Bounds;
    private List<SavedWindow> DraggedWindows() => incoming?.ToList()
        ?? (dragWindow == null ? [] : selected.Contains(dragWindow.Id) ? Selected.Where(DesktopOrder.Movable).ToList() : [dragWindow]);
    private void DrawGhost(Graphics g)
    {
        string text;
        if (dragging == DragKind.Desktop) text = "데스크톱 " + (dragSlot + 1);
        else if (DraggedWindows() is [var first, ..] dragged) text = AppName(first) + (dragged.Count > 1 ? $" 외 {dragged.Count - 1}개" : "");
        else return;
        var size = TextRenderer.MeasureText(text, Font);
        var r = new Rectangle(mouseAt.X + 14, mouseAt.Y + 10, size.Width + 16, size.Height + 8);
        using var path = Rounded(r, 6);
        using (var fill = new SolidBrush(Color.FromArgb(225, SystemColors.Highlight))) g.FillPath(fill, path);
        TextRenderer.DrawText(g, text, Font, r, SystemColors.HighlightText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath(); int d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure(); return path;
    }

    private (Card? Card, SavedWindow? Window) HitTest(Point client)
    {
        // An empty board shows no cards; a drop there keeps each window's own desktop.
        if (windows.Count == 0) return (null, null);
        var p = Client2Layout(client);
        foreach (var c in cards)
        {
            if (!c.Bounds.Contains(p)) continue;
            foreach (var (w, r) in c.Rows) if (r.Contains(p)) return (c, w);
            for (int i = c.Shapes.Count - 1; i >= 0; i--) if (c.Shapes[i].Bounds.Contains(p)) return (c, c.Shapes[i].Window);
            return (c, null);
        }
        return (null, null);
    }
    private bool Reorderable(Card? c) => c != null && c.Slot >= 0 && c.Slot < count;
    private void SelectOnly(SavedWindow w) { selected.Clear(); selected.Add(w.Id); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var (card, window) = HitTest(e.Location);
        bool ctrl = (ModifierKeys & Keys.Control) != 0;
        if (e.Button == MouseButtons.Right)
        {
            if (window != null && !selected.Contains(window.Id)) SelectOnly(window);
            Invalidate(); ShowMenu(card, window, e.Location); return;
        }
        if (e.Button != MouseButtons.Left) return;
        downAt = e.Location; pending = DragKind.None; dragWindow = null; dragSlot = -1;
        if (window != null)
        {
            downOnSelected = selected.Contains(window.Id);
            if (ctrl) { if (!selected.Remove(window.Id)) selected.Add(window.Id); }
            else if (!downOnSelected) SelectOnly(window);
            dragWindow = window;
            if (DesktopOrder.Movable(window) && selected.Contains(window.Id)) pending = DragKind.Window;
        }
        else
        {
            if (!ctrl) selected.Clear();
            if (Reorderable(card)) { pending = DragKind.Desktop; dragSlot = card!.Slot; }
        }
        Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        mouseAt = e.Location;
        if (dragging == DragKind.None && pending != DragKind.None && (e.Button & MouseButtons.Left) != 0)
        {
            var size = SystemInformation.DragSize;
            if (Math.Abs(e.X - downAt.X) > size.Width / 2 || Math.Abs(e.Y - downAt.Y) > size.Height / 2) { dragging = pending; tip.Hide(this); }
        }
        if (dragging != DragKind.None)
        {
            UpdateDropTarget(e.Location);
            ScrollNearEdge(e.Location);
            Cursor = dragging == DragKind.Window && dropSlot < 0 ? Cursors.No : Cursors.SizeAll;
            Invalidate(); return;
        }
        var (card, window) = HitTest(e.Location);
        if (window != hovered) { hovered = window; tip.SetToolTip(this, window == null ? "" : Describe(window)); }
        Cursor = window != null && DesktopOrder.Movable(window) || window == null && Reorderable(card) ? Cursors.Hand : Cursors.Default;
    }
    private void UpdateDropTarget(Point client)
    {
        var (card, _) = HitTest(client);
        if (dragging == DragKind.Window)
        {
            dropSlot = card is { Slot: >= 0 } ? card.Slot : -1;
            var at = Client2Layout(client);
            dropMonitor = dropSlot >= 0 && card!.Map.Contains(at) ? MonitorAt(card, at) : null;
            return;
        }
        var p = Client2Layout(client);
        var nearest = cards.Where(c => c.Slot >= 0 && c.Slot < count)
            .MinBy(c => Math.Pow(c.Bounds.Left + c.Bounds.Width / 2 - p.X, 2) + Math.Pow(c.Bounds.Top + c.Bounds.Height / 2 - p.Y, 2));
        int gap = nearest == null ? -1 : nearest.Slot + (p.X > nearest.Bounds.Left + nearest.Bounds.Width / 2 ? 1 : 0);
        // Dropping right beside itself would not move the desktop.
        dropGap = gap == dragSlot || gap == dragSlot + 1 ? -1 : gap;
    }
    private Rectangle? MonitorAt(Card c, Point p)
    {
        var at = new Point(screen.Left + (p.X - c.Map.Left) * screen.Width / Math.Max(1, c.Map.Width),
            screen.Top + (p.Y - c.Map.Top) * screen.Height / Math.Max(1, c.Map.Height));
        foreach (var m in monitors) if (m.Contains(at)) return m;
        return null;
    }
    // Puts a window where it was dropped: onto the card's desktop and, when dropped on a monitor of its screen, onto that monitor.
    private static void Place(SavedWindow w, int slot, Rectangle? monitor, string name)
    {
        if (monitor is { } to) DesktopOrder.MoveToMonitor(w, MonitorOf(w), to);
        if (slot >= 0 && DesktopOrder.Movable(w)) { w.DesktopIndex = slot; w.DesktopName = name; }
    }
    private void ScrollNearEdge(Point client)
    {
        int step = client.Y < 24 ? -24 : client.Y > ClientSize.Height - 24 ? 24 : 0;
        if (step != 0) AutoScrollPosition = new Point(0, Math.Max(0, -AutoScrollPosition.Y + step));
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        bool changed = false;
        if (dragging == DragKind.Window && dropSlot >= 0)
        {
            string name = DesktopOrder.NameOf(windows, dropSlot);
            foreach (var w in DraggedWindows())
            {
                var before = (w.DesktopIndex, w.Left, w.Top);
                Place(w, dropSlot, dropMonitor, name);
                changed |= before != (w.DesktopIndex, w.Left, w.Top);
            }
        }
        else if (dragging == DragKind.Desktop && dropGap >= 0)
        {
            DesktopOrder.Reorder(windows, dragSlot, dropGap > dragSlot ? dropGap - 1 : dropGap); changed = true;
        }
        else if (dragging == DragKind.None && dragWindow != null && downOnSelected && (ModifierKeys & Keys.Control) == 0) SelectOnly(dragWindow);
        EndDrag();
        if (changed) Changed?.Invoke();
    }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture && dragging != DragKind.None) EndDrag(); }
    private void EndDrag()
    {
        pending = dragging = DragKind.None; dragSlot = dropSlot = dropGap = -1; dropMonitor = null; incoming = null;
        Cursor = Cursors.Default; Invalidate();
    }
    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        incoming = e.Data?.GetData(typeof(SavedWindow[])) as SavedWindow[] is { Length: > 0 } dropped ? dropped : null;
        if (incoming == null) { e.Effect = DragDropEffects.None; return; }
        dragging = DragKind.Window; tip.Hide(this);
        OnDragOver(e);
    }
    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (incoming == null) return;
        e.Effect = DragDropEffects.Copy;
        mouseAt = PointToClient(new Point(e.X, e.Y));
        UpdateDropTarget(mouseAt); ScrollNearEdge(mouseAt); Invalidate();
    }
    protected override void OnDragLeave(EventArgs e) { base.OnDragLeave(e); EndDrag(); }
    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (incoming == null) return;
        var dropped = incoming; int slot = dropSlot; var monitor = dropMonitor;
        string name = DesktopOrder.NameOf(windows, slot);
        EndDrag();
        Dropped?.Invoke(dropped, w => Place(w, slot, monitor, name));
    }
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && HitTest(e.Location).Window is { } w) EditRequested?.Invoke(w);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape && dragging != DragKind.None) EndDrag();
        else if (e.KeyCode == Keys.Delete && selected.Count > 0) RemoveRequested?.Invoke();
        else if (e.KeyCode == Keys.Enter && Selected is [var one]) EditRequested?.Invoke(one);
        else if (e.Control && e.KeyCode == Keys.A) { selected.UnionWith(windows.Select(w => w.Id)); Invalidate(); }
        else return;
        e.Handled = true;
    }
    private void ShowMenu(Card? card, SavedWindow? window, Point at)
    {
        menu.Items.Clear();
        if (window != null)
        {
            var targets = Selected;
            menu.Items.Add("편집…", null, (_, _) => EditRequested?.Invoke(window));
            var move = new ToolStripMenuItem("데스크톱으로 이동") { Enabled = targets.Any(DesktopOrder.Movable) };
            for (int i = 0; i <= count; i++)
            {
                int slot = i;
                move.DropDownItems.Add(slot == count ? "새 데스크톱" : "데스크톱 " + (slot + 1), null, (_, _) => { DesktopOrder.MoveTo(targets, windows, slot); Changed?.Invoke(); });
            }
            menu.Items.Add(move);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(targets.Count > 1 ? $"프로필에서 제거 ({targets.Count}개)" : "프로필에서 제거", null, (_, _) => RemoveRequested?.Invoke());
        }
        else if (Reorderable(card))
        {
            int slot = card!.Slot;
            void Move(int to) { DesktopOrder.Reorder(windows, slot, to); Changed?.Invoke(); }
            menu.Items.Add("앞으로 이동", null, (_, _) => Move(slot - 1)).Enabled = slot > 0;
            menu.Items.Add("뒤로 이동", null, (_, _) => Move(slot + 1)).Enabled = slot < count - 1;
            // Moving an empty desktop past the last one drops it; later desktops shift forward.
            if (card.Rows.Count == 0) menu.Items.Add("빈 데스크톱 삭제", null, (_, _) => Move(count - 1));
        }
        if (menu.Items.Count > 0) menu.Show(this, at);
    }
    private string Describe(SavedWindow w)
    {
        string state = w.Maximized ? "최대화" : w.Minimized ? "최소화" : $"{w.Width}×{w.Height}";
        string opens = w.ProjectPath != "" ? "\n프로젝트: " + w.ProjectPath : w.OpenFiles.Count > 0 ? $"\n파일 {w.OpenFiles.Count}개" : "";
        string issue = IssueOf(w);
        return $"{w.Title}\n{AppName(w)} · {state}{opens}" + (issue != "" ? "\n⚠ " + issue : "")
            + (DesktopOrder.Movable(w) ? "\n끌어서 다른 데스크톱으로 옮길 수 있습니다." : "\n모든 데스크톱에 표시되는 창이라 옮길 수 없습니다.");
    }
}
