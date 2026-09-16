using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyVersion("1.8.4.5")]
[assembly: System.Reflection.AssemblyFileVersion("1.8.4.5")]

namespace FileOrderTimeTool
{
    // ===== UI 调整区：单位为 100% 缩放下的逻辑像素 =====
    static class Ui
    {
        public const int OuterMargin = 20;
        public const int RowHeight = 34;
        public const int RowGap = 6;
        public const int ButtonHeight = 28;
        public const int ActionButtonWidth = 82;
        public const int ControlGap = 7;
        public const int FieldGap = 7;
        public const int GroupGap = 27;
        public const int InputLeftInset = 6;
        public const int SelectorArrowWidth = 24;
        public const int SelectorTrailingGap = 7;
        public const int MinimumHeight = 520;
        public const int CardWidth = 152;
        public const int CardHeight = 186;
        public const int CardGap = 10;

        public static float ScaleFormOnce(Form form)
        {
            using (Graphics graphics = form.CreateGraphics())
            {
                float scale = graphics.DpiX / 96f;
                if (Math.Abs(scale - 1f) > 0.01f) form.Scale(new SizeF(scale, scale));
                return scale;
            }
        }
    }

    // ===== 文本调整区：界面备注和说明文字可优先在这里修改 =====
    static class UiText
    {
        public const string DateFeatureDisabled = "请勾选“更改文件日期”选项";
        public const string DateFeatureTip = "启用后可批量修改文件的修改日期或创建日期数据。";
        public const string RenameFeatureTip = "启用后按左上→右下的顺序重命名全部或选中的文件。";
        public const string HelpApplicationQuestion = "问：这个应用是用来做什么的？\r\n答：本工具用于按照自定义排列顺序，批量更改文件的修改日期或创建日期，也可以批量修改文件名和扩展名。所有计划都可以先预览，实际文件更改在本次运行期间支持撤销和重做。";
        public const string HelpFirstQuestion = "问：如何快速知道某个板块或设置的功能？\r\n答：将鼠标悬停在该板块或设置上约 1 秒，即可查看功能说明。";
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (Mutex instanceMutex = new Mutex(true, "Local\\FileOrderTimeTool.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    NativeMethods.ActivateExistingInstance("批量文件排序/重命名工具", Application.ProductVersion);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                GC.KeepAlive(instanceMutex);
            }
        }
    }

    sealed class FileItem
    {
        public string Path;
        public Image Thumbnail;
        public bool ThumbnailRequested;
        public bool Selected;

        public string Name { get { return System.IO.Path.GetFileName(Path); } }
        public string Extension { get { return System.IO.Path.GetExtension(Path); } }
        public DateTime LastWriteTime { get { try { return File.GetLastWriteTime(Path); } catch { return DateTime.MinValue; } } }
        public DateTime CreationTime { get { try { return File.GetCreationTime(Path); } catch { return DateTime.MinValue; } } }
        public long Size { get { try { return new FileInfo(Path).Length; } catch { return 0; } } }
    }

    interface IHistoryAction
    {
        string Name { get; }
        bool Undo();
        bool Redo();
    }

    sealed class HistoryManager
    {
        private readonly Stack<IHistoryAction> undoStack = new Stack<IHistoryAction>();
        private readonly Stack<IHistoryAction> redoStack = new Stack<IHistoryAction>();
        public event EventHandler Changed;

        public bool CanUndo { get { return undoStack.Count > 0; } }
        public bool CanRedo { get { return redoStack.Count > 0; } }

        public void Push(IHistoryAction action)
        {
            undoStack.Push(action);
            redoStack.Clear();
            RaiseChanged();
        }

        public void Undo()
        {
            if (!CanUndo) return;
            IHistoryAction action = undoStack.Pop();
            if (action.Undo()) redoStack.Push(action);
            else undoStack.Push(action);
            RaiseChanged();
        }

        public void Redo()
        {
            if (!CanRedo) return;
            IHistoryAction action = redoStack.Pop();
            if (action.Redo()) undoStack.Push(action);
            else redoStack.Push(action);
            RaiseChanged();
        }

        public void Clear()
        {
            undoStack.Clear();
            redoStack.Clear();
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            if (Changed != null) Changed(this, EventArgs.Empty);
        }
    }

    sealed class OrderAction : IHistoryAction
    {
        private readonly FileCanvas canvas;
        private readonly List<FileItem> before;
        private readonly List<FileItem> after;
        private readonly string name;
        public OrderAction(FileCanvas canvas, List<FileItem> before, List<FileItem> after, string name)
        {
            this.canvas = canvas;
            this.before = new List<FileItem>(before);
            this.after = new List<FileItem>(after);
            this.name = name;
        }
        public string Name { get { return name; } }
        public bool Undo() { canvas.SetOrder(before); return true; }
        public bool Redo() { canvas.SetOrder(after); return true; }
    }

    sealed class FileState
    {
        public FileItem Item;
        public string Path;
        public DateTime LastWriteTime;
        public DateTime CreationTime;
        public bool ChangeLastWriteTime;
        public bool ChangeCreationTime;
        public string Identity;
    }

    sealed class DiskChangeAction
    {
        private readonly MainForm form;
        private readonly List<FileState> before;
        private readonly List<FileState> after;
        public DiskChangeAction(MainForm form, List<FileState> before, List<FileState> after)
        {
            this.form = form;
            this.before = Clone(before);
            this.after = Clone(after);
        }
        public bool Undo() { return form.ApplyDiskTransition(after, before); }
        public bool Redo() { return form.ApplyDiskTransition(before, after); }
        private static List<FileState> Clone(List<FileState> src)
        {
            List<FileState> dst = new List<FileState>();
            foreach (FileState s in src)
                dst.Add(new FileState { Item = s.Item, Path = s.Path, LastWriteTime = s.LastWriteTime,
                    CreationTime = s.CreationTime, ChangeLastWriteTime = s.ChangeLastWriteTime,
                    ChangeCreationTime = s.ChangeCreationTime, Identity = s.Identity });
            return dst;
        }
    }

    sealed class DiskHistoryManager
    {
        private readonly Stack<DiskChangeAction> undoStack = new Stack<DiskChangeAction>();
        private readonly Stack<DiskChangeAction> redoStack = new Stack<DiskChangeAction>();
        public event EventHandler Changed;
        public bool CanUndo { get { return undoStack.Count > 0; } }
        public bool CanRedo { get { return redoStack.Count > 0; } }

        public void Push(DiskChangeAction action)
        {
            undoStack.Push(action);
            redoStack.Clear();
            RaiseChanged();
        }

        public bool Undo()
        {
            if (!CanUndo) return false;
            DiskChangeAction action = undoStack.Peek();
            if (!action.Undo()) return false;
            undoStack.Pop();
            redoStack.Push(action);
            RaiseChanged();
            return true;
        }

        public bool Redo()
        {
            if (!CanRedo) return false;
            DiskChangeAction action = redoStack.Peek();
            if (!action.Redo()) return false;
            redoStack.Pop();
            undoStack.Push(action);
            RaiseChanged();
            return true;
        }

        private void RaiseChanged()
        {
            if (Changed != null) Changed(this, EventArgs.Empty);
        }
    }

    static class FileIdentityHelper
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME_NATIVE { public uint dwLowDateTime; public uint dwHighDateTime; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint dwFileAttributes;
            public FILETIME_NATIVE ftCreationTime;
            public FILETIME_NATIVE ftLastAccessTime;
            public FILETIME_NATIVE ftLastWriteTime;
            public uint dwVolumeSerialNumber;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint nNumberOfLinks;
            public uint nFileIndexHigh;
            public uint nFileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        public static string TryGet(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    BY_HANDLE_FILE_INFORMATION info;
                    if (!GetFileInformationByHandle(fs.SafeFileHandle, out info)) return null;
                    return info.dwVolumeSerialNumber.ToString("X8", CultureInfo.InvariantCulture) + ":" +
                        info.nFileIndexHigh.ToString("X8", CultureInfo.InvariantCulture) + info.nFileIndexLow.ToString("X8", CultureInfo.InvariantCulture);
                }
            }
            catch { return null; }
        }
    }

    sealed class ShortcutBinding
    {
        public Keys KeyCode;
        public Keys Modifiers;
        public bool TabPrefix;

        public ShortcutBinding Clone() { return new ShortcutBinding { KeyCode = KeyCode, Modifiers = Modifiers, TabPrefix = TabPrefix }; }
        public bool Matches(Keys keyData, bool tabDown)
        {
            Keys code = keyData & Keys.KeyCode;
            Keys mods = keyData & Keys.Modifiers;
            return code == KeyCode && mods == Modifiers && tabDown == TabPrefix;
        }
        public string Serialize()
        {
            return ((int)KeyCode).ToString(CultureInfo.InvariantCulture) + "," + ((int)Modifiers).ToString(CultureInfo.InvariantCulture) + "," + (TabPrefix ? "1" : "0");
        }
        public static ShortcutBinding Parse(string text)
        {
            string[] p = (text ?? "").Split(',');
            int key, mods;
            if (p.Length != 3 || !int.TryParse(p[0], out key) || !int.TryParse(p[1], out mods)) return null;
            return new ShortcutBinding { KeyCode = (Keys)key, Modifiers = (Keys)mods, TabPrefix = p[2] == "1" };
        }
        public override bool Equals(object obj)
        {
            ShortcutBinding b = obj as ShortcutBinding;
            return b != null && b.KeyCode == KeyCode && b.Modifiers == Modifiers && b.TabPrefix == TabPrefix;
        }
        public override int GetHashCode() { return ((int)KeyCode * 397) ^ (int)Modifiers ^ (TabPrefix ? 1 : 0); }
        public string Display()
        {
            List<string> parts = new List<string>();
            if ((Modifiers & Keys.Control) == Keys.Control) parts.Add("Ctrl");
            if ((Modifiers & Keys.Shift) == Keys.Shift) parts.Add("Shift");
            if ((Modifiers & Keys.Alt) == Keys.Alt) parts.Add("Alt");
            if (TabPrefix) parts.Add("Tab");
            parts.Add(KeyName(KeyCode));
            return string.Join("+", parts.ToArray());
        }
        private static string KeyName(Keys k)
        {
            if (k == Keys.Left) return "←";
            if (k == Keys.Right) return "→";
            if (k == Keys.Up) return "↑";
            if (k == Keys.Down) return "↓";
            if (k == Keys.Delete) return "Delete";
            if (k == Keys.Enter) return "Enter";
            return k.ToString();
        }
    }

    static class ShortcutIds
    {
        public const string Remove = "Remove";
        public const string Sort = "Sort";
        public const string Undo = "Undo";
        public const string Redo = "Redo";
        public const string Preview = "Preview";
        public const string Apply = "Apply";
        public const string UndoDisk = "UndoDisk";
        public const string RedoDisk = "RedoDisk";
        public const string MoveLeft = "MoveLeft";
        public const string MoveRight = "MoveRight";
        public const string MoveFront = "MoveFront";
        public const string MoveEnd = "MoveEnd";
        public const string PrevSort = "PrevSort";
        public const string NextSort = "NextSort";
        public const string PrevDirection = "PrevDirection";
        public const string NextDirection = "NextDirection";
        public const string Help = "Help";

        public static readonly string[] Order = new string[] { Remove, Sort, Undo, Redo, Preview, Apply, UndoDisk, RedoDisk, MoveLeft, MoveRight, MoveFront, MoveEnd, PrevSort, NextSort, PrevDirection, NextDirection, Help };
        public static string Title(string id)
        {
            if (id == Remove) return "移除";
            if (id == Sort) return "执行排序";
            if (id == Undo) return "撤销";
            if (id == Redo) return "重做";
            if (id == Preview) return "预览";
            if (id == Apply) return "应用更改";
            if (id == UndoDisk) return "撤销更改";
            if (id == RedoDisk) return "重做更改";
            if (id == MoveLeft) return "向左移动";
            if (id == MoveRight) return "向右移动";
            if (id == MoveFront) return "移到最前";
            if (id == MoveEnd) return "移到最后";
            if (id == PrevSort) return "上一个排序方式";
            if (id == NextSort) return "下一个排序方式";
            if (id == PrevDirection) return "切换为升序";
            if (id == NextDirection) return "切换为降序";
            if (id == Help) return "使用说明";
            return id;
        }
        public static Dictionary<string, ShortcutBinding> Defaults()
        {
            Dictionary<string, ShortcutBinding> d = new Dictionary<string, ShortcutBinding>();
            d[Remove] = B(Keys.Delete);
            d[Sort] = B(Keys.Enter);
            d[Undo] = B(Keys.Z, Keys.Control);
            d[Redo] = B(Keys.Z, Keys.Control | Keys.Shift);
            d[Preview] = B(Keys.Enter, Keys.Alt);
            d[Apply] = B(Keys.Enter, Keys.Shift);
            d[UndoDisk] = B(Keys.Z, Keys.Control | Keys.Alt);
            d[RedoDisk] = B(Keys.Z, Keys.Control | Keys.Shift | Keys.Alt);
            d[MoveLeft] = B(Keys.Left);
            d[MoveRight] = B(Keys.Right);
            d[MoveFront] = B(Keys.Up);
            d[MoveEnd] = B(Keys.Down);
            d[PrevSort] = B(Keys.Up, Keys.None, true);
            d[NextSort] = B(Keys.Down, Keys.None, true);
            d[PrevDirection] = B(Keys.Up, Keys.Control, true);
            d[NextDirection] = B(Keys.Down, Keys.Control, true);
            d[Help] = B(Keys.F1);
            return d;
        }
        private static ShortcutBinding B(Keys key, Keys mods = Keys.None, bool tab = false)
        {
            return new ShortcutBinding { KeyCode = key, Modifiers = mods, TabPrefix = tab };
        }
    }

    sealed class FileCanvas : ScrollableControl
    {
        public readonly List<FileItem> Items = new List<FileItem>();
        public HistoryManager History;
        public event EventHandler SelectionChanged;
        public event EventHandler OrderChanged;

        private float canvasScale = 1f;
        private int Px(int value) { return (int)Math.Round(value * canvasScale); }
        private int CardW { get { return Px(Ui.CardWidth); } }
        private int TileH { get { return Px(Ui.CardHeight); } }
        private int MinGap { get { return Px(Ui.CardGap); } }
        private int HorizontalOuterMargin { get { return Px(Ui.OuterMargin); } }
        private int VerticalMargin { get { return Px(10); } }
        private int ThumbW { get { return Px(128); } }
        private int ThumbH { get { return Px(118); } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            using (Graphics g = CreateGraphics()) canvasScale = g.DpiX / 96f;
            UpdateScrollSize();
        }
        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            hoverIndex = -1;
            itemToolTip.SetToolTip(this, null);
            Invalidate();
        }
        private int anchorIndex = -1;
        private bool mouseDown;
        private int mouseDownIndex = -1;
        private Point mouseDownPoint;
        private bool draggingItems;
        private int insertionIndex = -1;
        private bool marquee;
        private Rectangle marqueeRect;
        private HashSet<FileItem> marqueeBase = new HashSet<FileItem>();
        private bool collapseOnMouseUp;
        private readonly ToolTip itemToolTip = new ToolTip();
        private int hoverIndex = -1;
        public event Action<int> ItemOpenRequested;

        public FileCanvas()
        {
            DoubleBuffered = true;
            AutoScroll = true;
            AllowDrop = true;
            BackColor = Color.White;
            TabStop = true;
            SetStyle(ControlStyles.Selectable, true);
            itemToolTip.InitialDelay = 850;
            itemToolTip.ReshowDelay = 250;
            itemToolTip.AutoPopDelay = 6000;
            itemToolTip.ShowAlways = true;
        }

        public int SelectedCount { get { return Items.Count(x => x.Selected); } }
        public List<FileItem> SelectedItemsInOrder { get { return Items.Where(x => x.Selected).ToList(); } }

        public void AddFiles(IEnumerable<string> paths)
        {
            HashSet<string> existing = new HashSet<string>(Items.Select(x => SafeFullPath(x.Path)), StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (string raw in paths)
            {
                try
                {
                    if (Directory.Exists(raw)) continue;
                    if (!File.Exists(raw)) continue;
                    string full = SafeFullPath(raw);
                    if (existing.Contains(full)) continue;
                    FileItem item = new FileItem { Path = full };
                    Items.Add(item);
                    existing.Add(full);
                    added++;
                    QueueThumbnail(item);
                }
                catch { }
            }
            if (added > 0)
            {
                UpdateScrollSize();
                Invalidate();
                RaiseOrderChanged();
            }
        }

        public void SetOrder(IEnumerable<FileItem> order)
        {
            Items.Clear();
            Items.AddRange(order);
            UpdateScrollSize();
            Invalidate();
            RaiseOrderChanged();
        }

        public void ClearSelection()
        {
            bool changed = false;
            foreach (FileItem item in Items) if (item.Selected) { item.Selected = false; changed = true; }
            if (changed) { Invalidate(); RaiseSelectionChanged(); }
        }

        public void SelectAllItems()
        {
            foreach (FileItem item in Items) item.Selected = true;
            Invalidate();
            RaiseSelectionChanged();
        }

        public void MoveSelected(bool toFront)
        {
            if (SelectedCount == 0) return;
            List<FileItem> before = new List<FileItem>(Items);
            List<FileItem> sel = Items.Where(x => x.Selected).ToList();
            List<FileItem> unsel = Items.Where(x => !x.Selected).ToList();
            List<FileItem> after = new List<FileItem>();
            if (toFront) { after.AddRange(sel); after.AddRange(unsel); }
            else { after.AddRange(unsel); after.AddRange(sel); }
            if (!SameOrder(before, after))
            {
                SetOrder(after);
                if (History != null) History.Push(new OrderAction(this, before, after, toFront ? "移动到最前" : "移动到最后"));
            }
        }

        public void MoveSelectedOne(bool left)
        {
            if (SelectedCount == 0 || Items.Count <= 1) return;
            List<FileItem> before = new List<FileItem>(Items);
            List<FileItem> selected = Items.Where(x => x.Selected).ToList();
            List<FileItem> remaining = Items.Where(x => !x.Selected).ToList();
            int first = -1, last = -1;
            for (int i = 0; i < Items.Count; i++)
            {
                if (!Items[i].Selected) continue;
                if (first < 0) first = i;
                last = i;
            }
            if (first < 0) return;
            int target;
            if (left) target = Math.Max(0, first - 1);
            else target = Math.Min(remaining.Count, last);
            remaining.InsertRange(target, selected);
            if (!SameOrder(before, remaining))
            {
                SetOrder(remaining);
                if (History != null) History.Push(new OrderAction(this, before, remaining, left ? "向左移动" : "向右移动"));
            }
        }

        public void RemoveSelected()
        {
            if (SelectedCount == 0) return;
            List<FileItem> before = new List<FileItem>(Items);
            List<FileItem> after = Items.Where(x => !x.Selected).ToList();
            SetOrder(after);
            if (History != null) History.Push(new OrderAction(this, before, after, "移除选中"));
            RaiseSelectionChanged();
        }

        public void ClearAll()
        {
            if (Items.Count == 0) return;
            List<FileItem> before = new List<FileItem>(Items);
            List<FileItem> after = new List<FileItem>();
            SetOrder(after);
            if (History != null) History.Push(new OrderAction(this, before, after, "清空"));
            RaiseSelectionChanged();
        }

        public void ApplySortedOrder(List<FileItem> sorted, string actionName)
        {
            List<FileItem> before = new List<FileItem>(Items);
            if (SameOrder(before, sorted)) return;
            SetOrder(sorted);
            if (History != null) History.Push(new OrderAction(this, before, sorted, actionName));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateScrollSize();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (Items.Count == 0)
            {
                TextRenderer.DrawText(e.Graphics, "请直接拖入文件进行导入", Font, ClientRectangle, Color.FromArgb(125, 125, 125),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }

            e.Graphics.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            for (int i = 0; i < Items.Count; i++) DrawItem(e.Graphics, i, Items[i]);

            if (draggingItems && insertionIndex >= 0)
            {
                Rectangle slot = GetSlotRect(insertionIndex);
                using (Pen p = new Pen(Color.FromArgb(0, 120, 215), 3))
                {
                    int x = slot.Left - 3;
                    e.Graphics.DrawLine(p, x, slot.Top + 4, x, slot.Bottom - 4);
                }
            }
            if (marquee)
            {
                using (Brush b = new SolidBrush(Color.FromArgb(35, 0, 120, 215))) e.Graphics.FillRectangle(b, marqueeRect);
                using (Pen p = new Pen(Color.FromArgb(0, 120, 215), 1)) e.Graphics.DrawRectangle(p, marqueeRect);
            }
        }

        private void DrawItem(Graphics g, int index, FileItem item)
        {
            Rectangle r = GetItemRect(index);
            Color back = item.Selected ? Color.FromArgb(218, 235, 252) : Color.White;
            Color border = item.Selected ? Color.FromArgb(0, 120, 215) : Color.FromArgb(225, 225, 225);
            using (Brush b = new SolidBrush(back)) g.FillRectangle(b, r);
            using (Pen p = new Pen(border, item.Selected ? 2 : 1)) g.DrawRectangle(p, r);

            Rectangle thumbRect = new Rectangle(r.Left + (r.Width - ThumbW) / 2, r.Top + Px(10), ThumbW, ThumbH);
            using (Brush b = new SolidBrush(Color.FromArgb(248, 248, 248))) g.FillRectangle(b, thumbRect);
            if (item.Thumbnail != null)
            {
                Rectangle fit = FitRect(item.Thumbnail.Size, thumbRect);
                g.DrawImage(item.Thumbnail, fit);
            }
            else
            {
                using (Brush b = new SolidBrush(Color.FromArgb(120, 120, 120)))
                using (Font f = new Font("Segoe UI", 9f))
                {
                    string ext = item.Extension.Length > 0 ? item.Extension.TrimStart('.').ToUpperInvariant() : "FILE";
                    StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(ext, f, b, thumbRect, sf);
                }
            }

            Rectangle textRect = new Rectangle(r.Left + Px(7), r.Top + Px(133), r.Width - Px(14), (int)Math.Ceiling(Font.GetHeight(g) * 2));
            using (Brush textBrush = new SolidBrush(Color.FromArgb(30, 30, 30)))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Near;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.LineLimit;
                g.DrawString(item.Name, Font, textBrush, textRect, sf);
            }
        }

        private Rectangle FitRect(Size image, Rectangle box)
        {
            if (image.Width <= 0 || image.Height <= 0) return box;
            double scale = Math.Min((double)box.Width / image.Width, (double)box.Height / image.Height);
            int w = Math.Max(1, (int)(image.Width * scale));
            int h = Math.Max(1, (int)(image.Height * scale));
            return new Rectangle(box.Left + (box.Width - w) / 2, box.Top + (box.Height - h) / 2, w, h);
        }

        private struct GridLayout
        {
            public int Columns;
            public float Gap;
            public float StartX;
            public float Stride;
        }

        private GridLayout GetGridLayout()
        {
            int viewport = Math.Max(CardW, ClientSize.Width);
            int usable = Math.Max(CardW, viewport - HorizontalOuterMargin * 2);
            int cols = Math.Max(1, (usable + MinGap) / (CardW + MinGap));
            while (cols > 1 && cols * CardW + (cols - 1) * MinGap > usable) cols--;

            float gap = 0f;
            float totalWidth = CardW;
            if (cols > 1)
            {
                gap = (usable - cols * CardW) / (float)(cols - 1);
                if (gap < MinGap) gap = MinGap;
                totalWidth = cols * CardW + (cols - 1) * gap;
            }

            float startX = Math.Max(0f, (viewport - totalWidth) / 2f);
            return new GridLayout { Columns = cols, Gap = gap, StartX = startX, Stride = CardW + gap };
        }

        private int Columns { get { return GetGridLayout().Columns; } }

        private Rectangle GetItemRect(int index)
        {
            GridLayout gl = GetGridLayout();
            int col = index % gl.Columns;
            int row = index / gl.Columns;
            int x = (int)Math.Round(gl.StartX + col * gl.Stride);
            return new Rectangle(x, VerticalMargin + row * TileH, CardW, TileH - Px(8));
        }

        private Rectangle GetSlotRect(int index)
        {
            GridLayout gl = GetGridLayout();
            int clamped = Math.Max(0, Math.Min(index, Items.Count));
            int col = clamped % gl.Columns;
            int row = clamped / gl.Columns;
            int x = (int)Math.Round(gl.StartX + col * gl.Stride);
            return new Rectangle(x, VerticalMargin + row * TileH, CardW, TileH - Px(8));
        }

        private int HitTest(Point clientPoint)
        {
            Point p = ClientToContent(clientPoint);
            for (int i = 0; i < Items.Count; i++) if (GetItemRect(i).Contains(p)) return i;
            return -1;
        }

        private int InsertionFromPoint(Point clientPoint)
        {
            Point p = ClientToContent(clientPoint);
            GridLayout gl = GetGridLayout();
            int row = Math.Max(0, (p.Y - VerticalMargin) / TileH);
            int rowStart = Math.Min(Items.Count, row * gl.Columns);
            int rowEnd = Math.Min(Items.Count, rowStart + gl.Columns);
            if (rowStart >= Items.Count) return Items.Count;

            // 行由卡片的完整垂直节距决定。鼠标仍在本行时，不会因为靠近
            // 卡片下半部而提前掉到下一行；行内再按各卡片中心线决定前/后。
            for (int i = rowStart; i < rowEnd; i++)
            {
                Rectangle card = GetItemRect(i);
                if (p.X < card.Left + card.Width / 2) return i;
            }
            return rowEnd;
        }

        private Point ClientToContent(Point p)
        {
            return new Point(p.X - AutoScrollPosition.X, p.Y - AutoScrollPosition.Y);
        }

        private void UpdateScrollSize()
        {
            int rows = Items.Count == 0 ? 0 : (int)Math.Ceiling((double)Items.Count / Columns);
            AutoScrollMinSize = new Size(0, VerticalMargin * 2 + rows * TileH);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Right)
            {
                ClearSelection();
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            mouseDown = true;
            mouseDownPoint = e.Location;
            mouseDownIndex = HitTest(e.Location);
            draggingItems = false;
            insertionIndex = -1;
            collapseOnMouseUp = false;
            bool ctrl = (ModifierKeys & Keys.Control) == Keys.Control;
            bool shift = (ModifierKeys & Keys.Shift) == Keys.Shift;

            if (mouseDownIndex >= 0)
            {
                if (shift && anchorIndex >= 0)
                {
                    if (!ctrl) foreach (FileItem x in Items) x.Selected = false;
                    int a = Math.Min(anchorIndex, mouseDownIndex);
                    int b = Math.Max(anchorIndex, mouseDownIndex);
                    for (int i = a; i <= b; i++) Items[i].Selected = true;
                    RaiseSelectionChanged();
                    Invalidate();
                }
                else if (ctrl)
                {
                    Items[mouseDownIndex].Selected = !Items[mouseDownIndex].Selected;
                    anchorIndex = mouseDownIndex;
                    RaiseSelectionChanged();
                    Invalidate();
                }
                else
                {
                    if (!Items[mouseDownIndex].Selected)
                    {
                        foreach (FileItem x in Items) x.Selected = false;
                        Items[mouseDownIndex].Selected = true;
                        RaiseSelectionChanged();
                        Invalidate();
                    }
                    else if (SelectedCount > 1)
                    {
                        collapseOnMouseUp = true;
                    }
                    anchorIndex = mouseDownIndex;
                }
            }
            else
            {
                marquee = true;
                Point cp = ClientToContent(e.Location);
                marqueeRect = new Rectangle(cp, Size.Empty);
                marqueeBase = ctrl ? new HashSet<FileItem>(Items.Where(x => x.Selected)) : new HashSet<FileItem>();
                if (!ctrl)
                {
                    foreach (FileItem x in Items) x.Selected = false;
                    RaiseSelectionChanged();
                    Invalidate();
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!mouseDown) UpdateHoverTooltip(e.Location);
            if (!mouseDown || e.Button != MouseButtons.Left) return;
            if (marquee)
            {
                Point a = ClientToContent(mouseDownPoint);
                Point b = ClientToContent(e.Location);
                marqueeRect = NormalizeRect(a, b);
                foreach (FileItem x in Items) x.Selected = marqueeBase.Contains(x);
                for (int i = 0; i < Items.Count; i++)
                    if (marqueeRect.IntersectsWith(GetItemRect(i))) Items[i].Selected = true;
                RaiseSelectionChanged();
                Invalidate();
                return;
            }

            if (mouseDownIndex >= 0 && Items[mouseDownIndex].Selected)
            {
                if (!draggingItems && (Math.Abs(e.X - mouseDownPoint.X) > 5 || Math.Abs(e.Y - mouseDownPoint.Y) > 5))
                {
                    draggingItems = true;
                    collapseOnMouseUp = false;
                }
                if (draggingItems)
                {
                    insertionIndex = InsertionFromPoint(e.Location);
                    Invalidate();
                    AutoScrollNearEdge(e.Location);
                }
            }
        }

        private void UpdateHoverTooltip(Point location)
        {
            int i = HitTest(location);
            if (i == hoverIndex) return;
            hoverIndex = i;
            itemToolTip.SetToolTip(this, i >= 0 && i < Items.Count ? Items[i].Name : null);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverIndex = -1;
            itemToolTip.SetToolTip(this, null);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (draggingItems && SelectedCount > 0)
            {
                List<FileItem> before = new List<FileItem>(Items);
                List<FileItem> moving = Items.Where(x => x.Selected).ToList();
                int target = insertionIndex < 0 ? Items.Count : insertionIndex;
                int selectedBefore = 0;
                for (int i = 0; i < Math.Min(target, Items.Count); i++) if (Items[i].Selected) selectedBefore++;
                List<FileItem> remaining = Items.Where(x => !x.Selected).ToList();
                int adjusted = Math.Max(0, Math.Min(remaining.Count, target - selectedBefore));
                remaining.InsertRange(adjusted, moving);
                if (!SameOrder(before, remaining))
                {
                    SetOrder(remaining);
                    if (History != null) History.Push(new OrderAction(this, before, remaining, "拖动排序"));
                }
            }
            else if (collapseOnMouseUp && mouseDownIndex >= 0)
            {
                foreach (FileItem x in Items) x.Selected = false;
                Items[mouseDownIndex].Selected = true;
                RaiseSelectionChanged();
                Invalidate();
            }

            mouseDown = false;
            mouseDownIndex = -1;
            draggingItems = false;
            insertionIndex = -1;
            marquee = false;
            collapseOnMouseUp = false;
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int i = HitTest(e.Location);
            if (i >= 0)
            {
                if (ItemOpenRequested != null) ItemOpenRequested(i);
                else try { Process.Start(Items[i].Path); } catch { }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Control && e.KeyCode == Keys.A)
            {
                SelectAllItems(); e.Handled = true;
            }
        }

        protected override void OnDragEnter(DragEventArgs drgevent)
        {
            base.OnDragEnter(drgevent);
            if (drgevent.Data != null && drgevent.Data.GetDataPresent(DataFormats.FileDrop)) drgevent.Effect = DragDropEffects.Copy;
        }

        protected override void OnDragOver(DragEventArgs drgevent)
        {
            base.OnDragOver(drgevent);
            if (drgevent.Data != null && drgevent.Data.GetDataPresent(DataFormats.FileDrop)) drgevent.Effect = DragDropEffects.Copy;
        }

        protected override void OnDragDrop(DragEventArgs drgevent)
        {
            base.OnDragDrop(drgevent);
            if (drgevent.Data == null || !drgevent.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] files = drgevent.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null) AddFiles(files);
        }

        private void AutoScrollNearEdge(Point p)
        {
            if (!AutoScroll) return;
            int y = -AutoScrollPosition.Y;
            if (p.Y < 30) y = Math.Max(0, y - 24);
            else if (p.Y > ClientSize.Height - 30) y += 24;
            AutoScrollPosition = new Point(-AutoScrollPosition.X, y);
        }

        private void QueueThumbnail(FileItem item)
        {
            if (item.ThumbnailRequested) return;
            item.ThumbnailRequested = true;
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                Image img = null;
                try { img = ShellThumbnail.GetThumbnail(item.Path, new Size(ThumbW, ThumbH)); } catch { }
                if (IsDisposed) { if (img != null) img.Dispose(); return; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        item.Thumbnail = img;
                        Invalidate();
                    });
                }
                catch { if (img != null) img.Dispose(); }
            });
        }

        private static Rectangle NormalizeRect(Point a, Point b)
        {
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        private static bool SameOrder(IList<FileItem> a, IList<FileItem> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!object.ReferenceEquals(a[i], b[i])) return false;
            return true;
        }

        private static string SafeFullPath(string path)
        {
            try { return System.IO.Path.GetFullPath(path); } catch { return path; }
        }

        private void RaiseSelectionChanged() { if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty); }
        private void RaiseOrderChanged() { if (OrderChanged != null) OrderChanged(this, EventArgs.Empty); }
    }

    static class ShellThumbnail
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; public SIZE(int x, int y) { cx = x; cy = y; } }

        [Flags]
        private enum SIIGBF
        {
            RESIZETOFIT = 0x00,
            BIGGERSIZEOK = 0x01,
            MEMORYONLY = 0x02,
            ICONONLY = 0x04,
            THUMBNAILONLY = 0x08,
            INCACHEONLY = 0x10
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc,
            [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [Out, MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        public static Image GetThumbnail(string path, Size size)
        {
            int coHr = CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED
            bool uninit = coHr >= 0;
            IShellItemImageFactory factory = null;
            IntPtr hbmp = IntPtr.Zero;
            try
            {
                Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, iid, out factory);
                if (hr != 0 || factory == null) return null;
                hr = factory.GetImage(new SIZE(size.Width, size.Height), SIIGBF.BIGGERSIZEOK, out hbmp);
                if (hr != 0 || hbmp == IntPtr.Zero)
                {
                    hr = factory.GetImage(new SIZE(size.Width, size.Height), SIIGBF.ICONONLY, out hbmp);
                }
                if (hr != 0 || hbmp == IntPtr.Zero) return null;
                using (Image temp = Image.FromHbitmap(hbmp))
                {
                    return new Bitmap(temp);
                }
            }
            finally
            {
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                if (factory != null) { try { Marshal.ReleaseComObject(factory); } catch { } }
                if (uninit) CoUninitialize();
            }
        }
    }

    sealed class SortMenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            int size = Math.Max(6, Math.Min(r.Width, r.Height) / 2);
            Rectangle dot = new Rectangle(r.Left + (r.Width - size) / 2, r.Top + (r.Height - size) / 2, size, size);
            using (Brush brush = new SolidBrush(SystemColors.Highlight)) e.Graphics.FillEllipse(brush, dot);
        }
    }

    sealed class SortSelector : Control
    {
        private bool pressed;
        private bool inactive;
        public bool UseTextEllipsis { get; set; }

        public bool Inactive
        {
            get { return inactive; }
            set { inactive = value; TabStop = !value; pressed = false; Invalidate(); }
        }

        public SortSelector()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.UserPaint, true);
            BackColor = SystemColors.Window;
            TabStop = true;
            UseTextEllipsis = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle bounds = ClientRectangle;
            if (bounds.Width <= 1 || bounds.Height <= 1) return;

            Color fill = Enabled && !inactive ? (pressed ? SystemColors.ControlLight : SystemColors.Window) : SystemColors.Control;
            using (Brush brush = new SolidBrush(fill)) e.Graphics.FillRectangle(brush, bounds);
            ControlPaint.DrawBorder(e.Graphics, bounds, Enabled && !inactive ? SystemColors.ControlDark : Color.FromArgb(171, 173, 179),
                ButtonBorderStyle.Solid);

            int inset = Math.Max(4, (int)Math.Round(Ui.InputLeftInset * e.Graphics.DpiX / 96f));
            int arrowArea = Math.Max(22, (int)Math.Round(24f * e.Graphics.DpiX / 96f));
            Rectangle textBounds = new Rectangle(inset, 1, Math.Max(0, Width - inset - arrowArea), Height - 2);
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
            if (UseTextEllipsis) flags |= TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, Enabled ? ForeColor : SystemColors.GrayText, flags);

            int centerX = Width - arrowArea / 2;
            int centerY = Height / 2;
            int arm = Math.Max(3, (int)Math.Round(4f * e.Graphics.DpiX / 96f));
            using (Pen pen = new Pen(Enabled && !inactive ? SystemColors.ControlText : SystemColors.GrayText, 1.4f))
            {
                pen.StartCap = LineCap.Square;
                pen.EndCap = LineCap.Square;
                e.Graphics.DrawLine(pen, centerX - arm, centerY - 2, centerX, centerY + 2);
                e.Graphics.DrawLine(pen, centerX, centerY + 2, centerX + arm, centerY - 2);
            }

            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -3, -3));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (inactive) return;
            if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (inactive) return;
            pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (inactive) { e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                OnClick(EventArgs.Empty);
                e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { pressed = false; base.OnLostFocus(e); Invalidate(); }
    }

    enum ActionIconKind { ApplySort, MoveFirst, MoveLast, Remove, Undo, Redo, UndoDisk, RedoDisk, Help }

    sealed class ActionIconButton : Button
    {
        public ActionIconKind IconKind { get; set; }

        public ActionIconButton()
        {
            Text = "";
            UseVisualStyleBackColor = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = Math.Min(ClientSize.Width, ClientSize.Height) / 28f;
            float ox = (ClientSize.Width - 20f * scale) / 2f;
            float oy = (ClientSize.Height - 20f * scale) / 2f;
            GraphicsState state = e.Graphics.Save();
            e.Graphics.TranslateTransform(ox, oy);
            e.Graphics.ScaleTransform(scale, scale);
            Color color = Enabled ? SystemColors.ControlText : SystemColors.GrayText;
            using (Pen pen = new Pen(color, 1f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                DrawIcon(e.Graphics, pen, IconKind);
            }
            e.Graphics.Restore(state);
        }

        private static void DrawIcon(Graphics g, Pen p, ActionIconKind kind)
        {
            if (kind == ActionIconKind.ApplySort)
            {
                g.DrawLines(p, new PointF[] { new PointF(3, 10), new PointF(8, 15), new PointF(17, 5) });
                return;
            }
            if (kind == ActionIconKind.MoveFirst || kind == ActionIconKind.MoveLast)
            {
                bool first = kind == ActionIconKind.MoveFirst;
                float barY = first ? 3 : 17;
                float tipY = first ? 5.5f : 14.5f;
                float tailY = first ? 17 : 3;
                g.DrawLine(p, 3, barY, 17, barY);
                g.DrawLine(p, 10, tailY, 10, tipY);
                g.DrawLine(p, 10, tipY, 6, first ? 9.5f : 10.5f);
                g.DrawLine(p, 10, tipY, 14, first ? 9.5f : 10.5f);
                return;
            }
            if (kind == ActionIconKind.Remove)
            {
                g.DrawLine(p, 3.5f, 5, 16.5f, 5);
                g.DrawLine(p, 7.5f, 2, 12.5f, 2);
                g.DrawRectangle(p, 5, 7.5f, 10, 10);
                return;
            }
            if (kind == ActionIconKind.Help)
            {
                g.DrawBezier(p, 6, 6, 6, 1.5f, 14, 1.5f, 14, 6);
                g.DrawBezier(p, 14, 6, 14, 9.5f, 10, 8.5f, 10, 12.5f);
                using (Brush dot = new SolidBrush(p.Color)) g.FillEllipse(dot, 9.1f, 15.1f, 1.8f, 1.8f);
                return;
            }
            bool redo = kind == ActionIconKind.Redo || kind == ActionIconKind.RedoDisk;
            bool disk = kind == ActionIconKind.UndoDisk || kind == ActionIconKind.RedoDisk;
            if (disk)
            {
                g.DrawLines(p, new PointF[] { new PointF(1.5f, 4), new PointF(8, 4), new PointF(10, 6), new PointF(18.5f, 6), new PointF(18.5f, 18), new PointF(1.5f, 18), new PointF(1.5f, 4) });
                DrawUndoArrow(g, p, redo, 5.5f, 7.5f, 9, 7);
            }
            else DrawUndoArrow(g, p, redo, 2.5f, 3.5f, 15, 13);
        }

        private static void DrawUndoArrow(Graphics g, Pen p, bool redo, float x, float y, float width, float height)
        {
            if (!redo)
            {
                g.DrawBezier(p, x + width, y + height, x + width, y, x + 3, y, x + 2, y + height * .55f);
                g.DrawLine(p, x + 2, y + height * .55f, x, y + height * .2f);
                g.DrawLine(p, x + 2, y + height * .55f, x + 5, y + height * .55f);
            }
            else
            {
                g.DrawBezier(p, x, y + height, x, y, x + width - 3, y, x + width - 2, y + height * .55f);
                g.DrawLine(p, x + width - 2, y + height * .55f, x + width, y + height * .2f);
                g.DrawLine(p, x + width - 2, y + height * .55f, x + width - 5, y + height * .55f);
            }
        }
    }

    sealed class LatestDateSelector : Control
    {
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly List<ToolStripMenuItem> items = new List<ToolStripMenuItem>();
        private int selectedMode;
        private DateTime customValue = DateTime.Now;
        private bool pressed;
        private bool inactive;
        public event EventHandler SelectionChanged;

        public bool Inactive
        {
            get { return inactive; }
            set { inactive = value; TabStop = !value; pressed = false; Invalidate(); }
        }

        public LatestDateSelector()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.UserPaint, true);
            BackColor = SystemColors.Window;
            TabStop = true;
            menu.ShowCheckMargin = true;
            menu.ShowImageMargin = false;
            menu.Renderer = new SortMenuRenderer();
            string[] names = new string[] { "文件的最晚日期", "当前时间", "自定义" };
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                ToolStripMenuItem item = new ToolStripMenuItem(names[i]);
                item.Click += delegate
                {
                    SelectedMode = index;
                    if (index == 2) EditCustomValue();
                };
                items.Add(item);
                menu.Items.Add(item);
            }
            menu.Closed += delegate { Focus(); };
            RefreshChecks();
        }

        public int SelectedMode
        {
            get { return selectedMode; }
            set
            {
                int next = Math.Max(0, Math.Min(2, value));
                if (selectedMode == next) { RefreshChecks(); Invalidate(); return; }
                selectedMode = next;
                RefreshChecks();
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
        }

        public DateTime CustomValue
        {
            get { return customValue; }
            set { customValue = value; Invalidate(); }
        }

        private string DisplayText
        {
            get
            {
                if (selectedMode == 1) return "当前时间";
                if (selectedMode == 2) return customValue.ToString("yyyy-MM-dd HH:mm:ss");
                return "文件的最晚日期";
            }
        }

        private int ArrowArea { get { return Math.Max(22, Height); } }

        private void RefreshChecks()
        {
            for (int i = 0; i < items.Count; i++) items[i].Checked = i == selectedMode;
        }

        private void ShowMenu()
        {
            menu.Font = Font;
            RefreshChecks();
            menu.Show(this, new Point(0, Height));
        }

        private void EditCustomValue()
        {
            using (CustomDateTimeForm form = new CustomDateTimeForm(customValue))
            {
                if (form.ShowDialog(FindForm()) != DialogResult.OK) return;
                customValue = form.SelectedValue;
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
        }

        public void Cycle(int step)
        {
            SelectedMode = (SelectedMode + step + 3) % 3;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle bounds = ClientRectangle;
            if (bounds.Width <= 1 || bounds.Height <= 1) return;
            using (Brush brush = new SolidBrush(Enabled && !inactive ? (pressed ? SystemColors.ControlLight : SystemColors.Window) : SystemColors.Control))
                e.Graphics.FillRectangle(brush, bounds);
            ControlPaint.DrawBorder(e.Graphics, bounds, Enabled && !inactive ? SystemColors.ControlDark : Color.FromArgb(171, 173, 179), ButtonBorderStyle.Solid);
            int inset = Math.Max(4, (int)Math.Round(Ui.InputLeftInset * e.Graphics.DpiX / 96f));
            Rectangle textBounds = new Rectangle(inset, 1, Math.Max(0, Width - inset - ArrowArea), Height - 2);
            TextRenderer.DrawText(e.Graphics, DisplayText, Font, textBounds, Enabled ? ForeColor : SystemColors.GrayText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            int centerX = Width - ArrowArea / 2;
            int centerY = Height / 2;
            int arm = Math.Max(3, (int)Math.Round(4f * e.Graphics.DpiX / 96f));
            using (Pen pen = new Pen(Enabled && !inactive ? SystemColors.ControlText : SystemColors.GrayText, 1.4f))
            {
                e.Graphics.DrawLine(pen, centerX - arm, centerY - 2, centerX, centerY + 2);
                e.Graphics.DrawLine(pen, centerX, centerY + 2, centerX + arm, centerY - 2);
            }
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -3, -3));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (inactive) return;
            if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (inactive) return;
            pressed = false;
            Invalidate();
            if (e.Button == MouseButtons.Left)
            {
                if (selectedMode == 2 && e.X < Width - ArrowArea) EditCustomValue();
                else ShowMenu();
            }
            base.OnMouseUp(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (inactive) { e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Enter && selectedMode == 2) { EditCustomValue(); e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space || (e.Alt && e.KeyCode == Keys.Down))
            { ShowMenu(); e.SuppressKeyPress = true; return; }
            base.OnKeyDown(e);
        }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { pressed = false; base.OnLostFocus(e); Invalidate(); }
    }

    sealed class CustomDateTimeForm : Form
    {
        private readonly DateTimePicker date = new DateTimePicker();
        private readonly DateTimePicker time = new DateTimePicker();
        public DateTime SelectedValue { get; private set; }

        public CustomDateTimeForm(DateTime value)
        {
            Text = "设置自定义日期和时间";
            ClientSize = new Size(365, 118);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            FlowLayoutPanel values = new FlowLayoutPanel();
            values.Dock = DockStyle.Top;
            values.Height = 54;
            values.Padding = new Padding(14, 14, 0, 0);
            values.WrapContents = false;
            date.Format = DateTimePickerFormat.Custom;
            date.CustomFormat = "yyyy-MM-dd";
            date.Width = 170;
            date.Value = value;
            time.Format = DateTimePickerFormat.Time;
            time.ShowUpDown = true;
            time.Width = 125;
            time.Value = value;
            values.Controls.Add(date);
            values.Controls.Add(time);
            Controls.Add(values);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 50;
            buttons.Padding = new Padding(0, 8, 12, 0);
            buttons.FlowDirection = FlowDirection.RightToLeft;
            Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 82 };
            Button ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 82 };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;
            Shown += delegate { date.Select(); };
            FormClosing += delegate
            {
                SelectedValue = date.Value.Date + time.Value.TimeOfDay;
            };
            Ui.ScaleFormOnce(this);
        }
    }

    sealed class MainForm : Form, IMessageFilter
    {
        private readonly HistoryManager history = new HistoryManager();
        private readonly DiskHistoryManager diskHistory = new DiskHistoryManager();
        private readonly FileCanvas canvas = new FileCanvas();
        private readonly Label lblOrder = new Label();
        private readonly Label lblDateModeTitle = new Label();
        private readonly SortSelector btnDateMode = new SortSelector();
        private readonly ContextMenuStrip dateModeMenu = new ContextMenuStrip();
        private readonly List<ToolStripMenuItem> dateTypeMenuItems = new List<ToolStripMenuItem>();
        private readonly List<ToolStripMenuItem> dateDirectionMenuItems = new List<ToolStripMenuItem>();
        private readonly Label lblLatestDateTitle = new Label();
        private readonly LatestDateSelector latestDateSelector = new LatestDateSelector();
        private readonly Label lblIntervalTitle = new Label();
        private readonly NumericUpDown numInterval = new NumericUpDown();
        private readonly ComboBox cmbUnit = new ComboBox();
        private readonly SortSelector btnUnit = new SortSelector();
        private readonly ContextMenuStrip unitMenu = new ContextMenuStrip();
        private readonly List<ToolStripMenuItem> unitMenuItems = new List<ToolStripMenuItem>();
        private readonly ComboBox cmbSort = new ComboBox();
        private readonly ComboBox cmbDirection = new ComboBox();
        private readonly SortSelector btnSortMode = new SortSelector();
        private readonly ContextMenuStrip sortMenu = new ContextMenuStrip();
        private readonly List<ToolStripMenuItem> sortTypeMenuItems = new List<ToolStripMenuItem>();
        private readonly List<ToolStripMenuItem> sortDirectionMenuItems = new List<ToolStripMenuItem>();
        private readonly ActionIconButton btnHelp = new ActionIconButton { IconKind = ActionIconKind.Help };
        private readonly ActionIconButton btnSort = new ActionIconButton { IconKind = ActionIconKind.ApplySort };
        private readonly ActionIconButton btnFront = new ActionIconButton { IconKind = ActionIconKind.MoveFirst };
        private readonly ActionIconButton btnEnd = new ActionIconButton { IconKind = ActionIconKind.MoveLast };
        private readonly ActionIconButton btnRemove = new ActionIconButton { IconKind = ActionIconKind.Remove };
        private readonly ActionIconButton btnUndo = new ActionIconButton { IconKind = ActionIconKind.Undo };
        private readonly ActionIconButton btnRedo = new ActionIconButton { IconKind = ActionIconKind.Redo };
        private readonly ActionIconButton btnUndoDisk = new ActionIconButton { IconKind = ActionIconKind.UndoDisk };
        private readonly ActionIconButton btnRedoDisk = new ActionIconButton { IconKind = ActionIconKind.RedoDisk };
        private readonly CheckBox chkDate = new CheckBox();
        private readonly CheckBox chkExtension = new CheckBox();
        private readonly TextBox txtExtension = new TextBox();
        private readonly CheckBox chkRename = new CheckBox();
        private readonly TextBox txtTemplate = new TextBox();
        private readonly Button btnSetIncrement = new Button();
        private readonly Label lblIncrement = new Label();
        private readonly Button btnShortcutSettings = new Button();
        private readonly Button btnPreview = new Button();
        private readonly Button btnApply = new Button();
        private readonly ToolTip toolTip = new ToolTip();
        private readonly System.Windows.Forms.Timer disabledToolTipTimer = new System.Windows.Forms.Timer();
        private Control disabledToolTipTarget;
        private bool disabledToolTipVisible;
        private Dictionary<string, ShortcutBinding> shortcuts = ShortcutIds.Defaults();
        private int renameIndex = -1;
        private int renameLength = 0;
        private long renameBase = 0;
        private bool ignoreTemplateChange = false;
        private bool timeDescending = true;
        private bool changeCreationDate;
        private bool helpShown;
        private float uiScale = 1f;
        private bool restoreMaximized;
        private TableLayoutPanel topFirstLeft;
        private TableLayoutPanel topAreaLayout;
        private TableLayoutPanel topSecondLeft;
        private TableLayoutPanel topSecondRight;
        private TableLayoutPanel bottomFirstRow;

        private string SettingsDir { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileOrderTimeTool"); } }
        private string SettingsPath { get { return System.IO.Path.Combine(SettingsDir, "settings.ini"); } }

        public MainForm()
        {
            Text = "批量文件排序/重命名工具";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Width = 1180;
            Height = 820;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;

            toolTip.InitialDelay = 1000;
            toolTip.ReshowDelay = 1000;
            toolTip.AutoPopDelay = 7000;
            toolTip.ShowAlways = true;
            disabledToolTipTimer.Interval = 1000;
            disabledToolTipTimer.Tick += delegate
            {
                disabledToolTipTimer.Stop();
                ShowDisabledControlToolTip();
            };

            canvas.History = history;
            BuildUi();
            // 无设计器：在完整控件树建立后只缩放一次，避免构造途中记录错误基准。
            // manifest 声明 System DPI aware；跨不同 DPI 显示器后重新启动。
            uiScale = Ui.ScaleFormOnce(this);
            NormalizeTopControlMetrics();
            LoadSettings();
            WireEvents();
            ClearIncrementRule();
            UpdateUiState();
            PerformLayout();
            UpdateMinimumWindowSize();
            Shown += delegate
            {
                CenterToScreen();
                if (restoreMaximized) WindowState = FormWindowState.Maximized;
                ApplyLeftInsets();
                canvas.Focus();
                if (!helpShown)
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        helpShown = true;
                        SaveSettings();
                        ShowHelp();
                    });
                }
            };
        }

        private void BuildUi()
        {
            SuspendLayout();

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Margin = new Padding(0);
            root.Padding = new Padding(0);
            root.RowCount = 3;
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // 顶部：两行，统一左右边距、控件基线和行距。
            TableLayoutPanel topArea = new TableLayoutPanel();
            topAreaLayout = topArea;
            topArea.Dock = DockStyle.Fill;
            topArea.AutoSize = true;
            topArea.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            topArea.Margin = new Padding(0);
            topArea.Padding = new Padding(Ui.OuterMargin, 10, Ui.OuterMargin, 10);
            topArea.ColumnCount = 1;
            topArea.RowCount = 3;
            topArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            topArea.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.RowHeight));
            topArea.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.RowGap));
            topArea.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.RowHeight));

            TableLayoutPanel top1 = new TableLayoutPanel();
            top1.Dock = DockStyle.Fill;
            top1.Margin = new Padding(0);
            top1.Padding = new Padding(0);
            top1.ColumnCount = 3;
            top1.RowCount = 1;
            top1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top1.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.RowHeight));

            topFirstLeft = NewSingleRowTable();
            lblOrder.AutoSize = true;
            lblOrder.Font = new Font(Font, FontStyle.Bold);
            AddRowControl(topFirstLeft, lblOrder, Ui.GroupGap);

            lblDateModeTitle.Text = "修改模式：";
            lblDateModeTitle.AutoSize = true;
            lblDateModeTitle.Font = new Font(Font, FontStyle.Bold);
            AddRowControl(topFirstLeft, lblDateModeTitle, 0);

            btnDateMode.Width = SelectorWidth("修改日期 - 降序");
            AddRowControl(topFirstLeft, btnDateMode, Ui.GroupGap);

            lblLatestDateTitle.Text = "最晚日期：";
            lblLatestDateTitle.AutoSize = true;
            AddRowControl(topFirstLeft, lblLatestDateTitle, 0);

            latestDateSelector.Width = SelectorWidth("0000-00-00 00:00:00");
            latestDateSelector.Height = Ui.ButtonHeight;
            AddRowControl(topFirstLeft, latestDateSelector, Ui.GroupGap);

            lblIntervalTitle.Text = "间隔：";
            lblIntervalTitle.AutoSize = true;
            AddRowControl(topFirstLeft, lblIntervalTitle, 0);

            numInterval.Minimum = 1;
            numInterval.Maximum = 999999;
            numInterval.Width = 70;
            AddRowControl(topFirstLeft, numInterval, Ui.FieldGap);

            cmbUnit.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbUnit.Items.AddRange(new object[] { "秒", "分" });
            btnUnit.UseTextEllipsis = false;
            btnUnit.Width = SelectorWidth("秒");
            AddRowControl(topFirstLeft, btnUnit, 0);
            BuildDateModeMenu();
            BuildUnitMenu();
            top1.Controls.Add(topFirstLeft, 0, 0);

            btnHelp.Text = "";
            btnHelp.AccessibleName = "使用说明";
            btnHelp.Width = Ui.ButtonHeight;
            btnHelp.Height = Ui.ButtonHeight;
            btnHelp.Margin = new Padding(Ui.GroupGap, 0, 0, 0);
            btnHelp.Anchor = AnchorStyles.Right;
            top1.Controls.Add(btnHelp, 2, 0);
            topArea.Controls.Add(top1, 0, 0);

            TableLayoutPanel top2 = new TableLayoutPanel();
            top2.Dock = DockStyle.Fill;
            top2.AutoSize = true;
            top2.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            top2.Margin = new Padding(0);
            top2.Padding = new Padding(0);
            top2.ColumnCount = 3;
            top2.RowCount = 1;
            top2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            TableLayoutPanel sortRow = NewSingleRowTable();
            topSecondLeft = sortRow;
            Label lblSort = new Label { Text = "排序：", AutoSize = true };
            AddRowControl(sortRow, lblSort, 0);

            cmbSort.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSort.Items.AddRange(new object[] { "文件名", "修改日期", "创建日期", "文件大小", "文件类型" });
            cmbDirection.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbDirection.Items.AddRange(new object[] { "升序", "降序" });

            btnSortMode.Width = SelectorWidth("修改日期 - 降序");
            AddRowControl(sortRow, btnSortMode, Ui.FieldGap);
            BuildSortMenu();

            SetActionIconButton(btnSort, "排序");
            AddRowControl(sortRow, btnSort, Ui.GroupGap);

            SetActionIconButton(btnFront, "移到最前");
            AddRowControl(sortRow, btnFront, Ui.ControlGap);

            SetActionIconButton(btnEnd, "移到最后");
            AddRowControl(sortRow, btnEnd, Ui.ControlGap);

            SetActionIconButton(btnRemove, "移除");
            AddRowControl(sortRow, btnRemove, Ui.ControlGap);

            SetActionIconButton(btnUndo, "撤销");
            AddRowControl(sortRow, btnUndo, Ui.ControlGap);

            SetActionIconButton(btnRedo, "重做");
            AddRowControl(sortRow, btnRedo, 0);
            top2.Controls.Add(sortRow, 0, 0);

            // 空百分比列提供弹性间距。

            TableLayoutPanel historyRow = NewSingleRowTable();
            topSecondRight = historyRow;
            SetActionIconButton(btnUndoDisk, "撤销更改");
            SetActionIconButton(btnRedoDisk, "重做更改");
            AddRowControl(historyRow, btnUndoDisk, Ui.ControlGap);
            AddRowControl(historyRow, btnRedoDisk, 0);
            top2.Controls.Add(historyRow, 2, 0);
            topArea.Controls.Add(top2, 0, 2);
            root.Controls.Add(topArea, 0, 0);

            canvas.Dock = DockStyle.Fill;
            canvas.Margin = new Padding(0);
            root.Controls.Add(canvas, 0, 1);

            // 底部：两行固定逻辑布局。左右边距与顶部完全一致。
            TableLayoutPanel bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Fill;
            bottom.AutoSize = true;
            bottom.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            bottom.Margin = new Padding(0);
            bottom.Padding = new Padding(Ui.OuterMargin, 10, Ui.OuterMargin, 10);
            bottom.ColumnCount = 1;
            bottom.RowCount = 3;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.RowHeight));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.RowGap));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.RowHeight));
            bottom.BackColor = Color.FromArgb(247, 247, 247);

            TableLayoutPanel renameRow = NewSingleRowTable();
            bottomFirstRow = renameRow;
            chkDate.Text = "更改文件日期";
            chkDate.Checked = true;
            chkDate.AutoSize = true;
            AddRowControl(renameRow, chkDate, Ui.GroupGap);

            chkExtension.Text = "更改扩展名";
            chkExtension.AutoSize = true;
            AddRowControl(renameRow, chkExtension, Ui.FieldGap);

            txtExtension.Width = 70;
            AddRowControl(renameRow, txtExtension, Ui.GroupGap);

            chkRename.Text = "批量重命名";
            chkRename.AutoSize = true;
            AddRowControl(renameRow, chkRename, Ui.FieldGap);

            txtTemplate.Width = 220;
            AddRowControl(renameRow, txtTemplate, Ui.FieldGap);

            btnSetIncrement.Text = "设置递增";
            btnSetIncrement.Width = 90;
            btnSetIncrement.Height = 27;
            AddRowControl(renameRow, btnSetIncrement, Ui.FieldGap);

            lblIncrement.AutoSize = false;
            lblIncrement.Width = 250;
            lblIncrement.Height = Ui.ButtonHeight;
            lblIncrement.TextAlign = ContentAlignment.MiddleLeft;
            lblIncrement.AutoEllipsis = true;
            AddRowControl(renameRow, lblIncrement, 0);
            renameRow.ColumnStyles[renameRow.ColumnCount - 1].SizeType = SizeType.Percent;
            renameRow.ColumnStyles[renameRow.ColumnCount - 1].Width = 100;
            lblIncrement.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            bottom.Controls.Add(renameRow, 0, 0);

            TableLayoutPanel actionRow = new TableLayoutPanel();
            actionRow.Dock = DockStyle.Fill;
            actionRow.AutoSize = true;
            actionRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            actionRow.Margin = new Padding(0);
            actionRow.Padding = new Padding(0);
            actionRow.ColumnCount = 3;
            actionRow.RowCount = 1;
            actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            btnShortcutSettings.Text = "快捷键设置";
            btnShortcutSettings.Width = 110;
            btnShortcutSettings.Height = Ui.ButtonHeight;
            btnShortcutSettings.Anchor = AnchorStyles.Left;
            btnShortcutSettings.Margin = new Padding(0);
            actionRow.Controls.Add(btnShortcutSettings, 0, 0);

            Label hint = new Label();
            hint.Text = "重命名：框选数字后点设置递增；文件重命名顺序默认升序；鼠标悬在按钮上可查看快捷键";
            hint.Font = new Font(Font.FontFamily, 8f);
            hint.ForeColor = Color.DimGray;
            toolTip.SetToolTip(hint, hint.Text);
            hint.AutoSize = false;
            hint.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            hint.Height = Ui.ButtonHeight;
            hint.AutoEllipsis = true;
            hint.TextAlign = ContentAlignment.MiddleLeft;
            hint.Margin = new Padding(18, 0, 12, 0);
            actionRow.Controls.Add(hint, 1, 0);

            TableLayoutPanel actionButtons = NewSingleRowTable();
            btnPreview.Text = "预览";
            btnPreview.Width = 90;
            btnPreview.Height = 32;
            btnApply.Text = "应用更改";
            btnApply.Width = 110;
            btnApply.Height = 32;
            AddRowControl(actionButtons, btnPreview, 8);
            AddRowControl(actionButtons, btnApply, 0);
            actionRow.Controls.Add(actionButtons, 2, 0);
            bottom.Controls.Add(actionRow, 0, 2);
            root.Controls.Add(bottom, 0, 2);

            ResumeLayout(true);
        }

        private int SelectorWidth(string longestText)
        {
            using (Graphics graphics = CreateGraphics())
            {
                float scale = Math.Max(1f, graphics.DpiX / 96f);
                int physical = TextRenderer.MeasureText(graphics, longestText, Font, new Size(int.MaxValue, Ui.ButtonHeight),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                int logicalText = (int)Math.Ceiling(physical / scale);
                return logicalText + Ui.InputLeftInset + Ui.SelectorArrowWidth + Ui.SelectorTrailingGap;
            }
        }

        private void BuildDateModeMenu()
        {
            dateModeMenu.ShowCheckMargin = true;
            dateModeMenu.ShowImageMargin = false;
            dateModeMenu.Font = Font;
            dateModeMenu.Renderer = new SortMenuRenderer();
            string[] types = new string[] { "修改日期", "创建日期" };
            for (int i = 0; i < types.Length; i++)
            {
                int index = i;
                ToolStripMenuItem item = new ToolStripMenuItem(types[i]);
                item.Click += delegate { SetDateType(index); FocusCanvasSoon(); };
                dateTypeMenuItems.Add(item);
                dateModeMenu.Items.Add(item);
            }
            dateModeMenu.Items.Add(new ToolStripSeparator());
            string[] directions = new string[] { "升序", "降序" };
            for (int i = 0; i < directions.Length; i++)
            {
                int index = i;
                ToolStripMenuItem item = new ToolStripMenuItem(directions[i]);
                item.Click += delegate { SetDateDirection(index); FocusCanvasSoon(); };
                dateDirectionMenuItems.Add(item);
                dateModeMenu.Items.Add(item);
            }
            dateModeMenu.Closed += delegate { FocusCanvasSoon(); };
        }

        private void BuildUnitMenu()
        {
            unitMenu.ShowCheckMargin = true;
            unitMenu.ShowImageMargin = false;
            unitMenu.Font = Font;
            unitMenu.Renderer = new SortMenuRenderer();
            for (int i = 0; i < cmbUnit.Items.Count; i++)
            {
                int index = i;
                ToolStripMenuItem item = new ToolStripMenuItem(cmbUnit.Items[i].ToString());
                item.Click += delegate { SetUnit(index); FocusCanvasSoon(); };
                unitMenuItems.Add(item);
                unitMenu.Items.Add(item);
            }
            unitMenu.Closed += delegate { FocusCanvasSoon(); };
        }

        private void BuildSortMenu()
        {
            sortMenu.ShowCheckMargin = true;
            sortMenu.ShowImageMargin = false;
            sortMenu.Font = Font;
            sortMenu.Renderer = new SortMenuRenderer();
            for (int i = 0; i < cmbSort.Items.Count; i++)
            {
                int capturedIndex = i;
                ToolStripMenuItem item = new ToolStripMenuItem(cmbSort.Items[i].ToString());
                item.Click += delegate
                {
                    cmbSort.SelectedIndex = capturedIndex;
                    RefreshSortMenu();
                    FocusCanvasSoon();
                };
                sortTypeMenuItems.Add(item);
                sortMenu.Items.Add(item);
            }
            sortMenu.Items.Add(new ToolStripSeparator());
            string[] directions = new string[] { "升序", "降序" };
            for (int i = 0; i < directions.Length; i++)
            {
                int capturedIndex = i;
                ToolStripMenuItem item = new ToolStripMenuItem(directions[i]);
                item.Click += delegate
                {
                    cmbDirection.SelectedIndex = capturedIndex;
                    RefreshSortMenu();
                    FocusCanvasSoon();
                };
                sortDirectionMenuItems.Add(item);
                sortMenu.Items.Add(item);
            }
            sortMenu.Closed += delegate { FocusCanvasSoon(); };
        }

        private void RefreshSortMenu()
        {
            int sortIndex = Math.Max(0, cmbSort.SelectedIndex);
            int directionIndex = Math.Max(0, cmbDirection.SelectedIndex);
            for (int i = 0; i < sortTypeMenuItems.Count; i++) sortTypeMenuItems[i].Checked = i == sortIndex;
            for (int i = 0; i < sortDirectionMenuItems.Count; i++) sortDirectionMenuItems[i].Checked = i == directionIndex;
            string sortName = cmbSort.Items.Count > sortIndex ? cmbSort.Items[sortIndex].ToString() : "文件名";
            string directionName = cmbDirection.Items.Count > directionIndex ? cmbDirection.Items[directionIndex].ToString() : "升序";
            btnSortMode.Text = sortName + " - " + directionName;
            btnSortMode.Invalidate();
        }

        private void RefreshDateModeMenu()
        {
            int typeIndex = changeCreationDate ? 1 : 0;
            int directionIndex = timeDescending ? 1 : 0;
            for (int i = 0; i < dateTypeMenuItems.Count; i++) dateTypeMenuItems[i].Checked = i == typeIndex;
            for (int i = 0; i < dateDirectionMenuItems.Count; i++) dateDirectionMenuItems[i].Checked = i == directionIndex;
            btnDateMode.Text = (changeCreationDate ? "创建日期" : "修改日期") + " - " + (timeDescending ? "降序" : "升序");
            btnDateMode.Invalidate();
        }

        private void RefreshUnitMenu()
        {
            int index = Math.Max(0, cmbUnit.SelectedIndex);
            for (int i = 0; i < unitMenuItems.Count; i++) unitMenuItems[i].Checked = i == index;
            btnUnit.Text = cmbUnit.Items.Count > index ? cmbUnit.Items[index].ToString() : "秒";
            btnUnit.Invalidate();
        }

        private void SetDateType(int index)
        {
            changeCreationDate = index == 1;
            RefreshDateModeMenu();
            UpdateTooltips();
            SaveSettings();
        }

        private void SetDateDirection(int index)
        {
            timeDescending = index == 1;
            RefreshDateModeMenu();
            UpdateTooltips();
            SaveSettings();
        }

        private void SetUnit(int index)
        {
            if (cmbUnit.Items.Count == 0) return;
            cmbUnit.SelectedIndex = Math.Max(0, Math.Min(cmbUnit.Items.Count - 1, index));
            RefreshUnitMenu();
            SaveSettings();
        }

        private void FocusCanvasSoon()
        {
            if (!IsHandleCreated || IsDisposed) return;
            BeginInvoke((MethodInvoker)delegate { if (!canvas.IsDisposed) canvas.Focus(); });
        }

        private void UpdateMinimumWindowSize()
        {
            if (topFirstLeft == null || topSecondLeft == null || topSecondRight == null || topAreaLayout == null) return;
            int firstRowWidth = topFirstLeft.GetPreferredSize(Size.Empty).Width + btnHelp.Width +
                btnHelp.Margin.Horizontal + topAreaLayout.Padding.Horizontal;
            int secondRowWidth = topSecondLeft.GetPreferredSize(Size.Empty).Width +
                topSecondRight.GetPreferredSize(Size.Empty).Width + topAreaLayout.Padding.Horizontal;
            int bottomRowWidth = 0;
            if (bottomFirstRow != null)
            {
                int fixedControlsWidth = 0;
                foreach (Control control in bottomFirstRow.Controls)
                    if (control != lblIncrement) fixedControlsWidth += control.Width + control.Margin.Horizontal;
                TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                int unsetWidth = TextRenderer.MeasureText("未设置递增区域", lblIncrement.Font, Size.Empty, flags).Width;
                int setWidth = TextRenderer.MeasureText("已设置递增区域", lblIncrement.Font, Size.Empty, flags).Width;
                int statusWidth = Math.Max(unsetWidth, setWidth);
                bottomRowWidth = fixedControlsWidth + statusWidth + topAreaLayout.Padding.Horizontal;
            }
            int clientWidth = Math.Max(firstRowWidth, Math.Max(secondRowWidth, bottomRowWidth));
            int nonClientWidth = Width - ClientSize.Width;
            int nonClientHeight = Height - ClientSize.Height;
            MinimumSize = new Size(clientWidth + nonClientWidth, (int)Math.Round(Ui.MinimumHeight * uiScale) + nonClientHeight);
            if (Width < MinimumSize.Width) Width = MinimumSize.Width;
            Rectangle work = Screen.FromControl(this).WorkingArea;
            if (Width > work.Width || Height > work.Height)
                Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
        }

        private TableLayoutPanel NewSingleRowTable()
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Fill;
            t.AutoSize = true;
            t.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            t.Margin = new Padding(0);
            t.Padding = new Padding(0);
            t.RowCount = 1;
            t.ColumnCount = 0;
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.RowHeight));
            return t;
        }

        private void AddRowControl(TableLayoutPanel table, Control control, int rightMargin)
        {
            int col = table.ColumnCount;
            table.ColumnCount = col + 1;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            if (control is Button) control.Height = Ui.ButtonHeight;
            control.Anchor = AnchorStyles.Left;
            control.Margin = new Padding(0, 0, rightMargin, 0);
            table.Controls.Add(control, col, 0);
        }

        private void AddFillColumn(TableLayoutPanel table)
        {
            int col = table.ColumnCount;
            table.ColumnCount = col + 1;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            // 不放置带默认尺寸的占位面板。
        }

        private void SetActionIconButton(ActionIconButton button, string accessibleName)
        {
            button.Text = "";
            button.AccessibleName = accessibleName;
            button.Width = Ui.ButtonHeight;
            button.Height = Ui.ButtonHeight;
        }

        private void NormalizeTopControlMetrics()
        {
            // NumericUpDown uses a DPI-aware native preferred height.  Treat it as
            // the single source of truth so the custom selectors cannot collapse
            // to their default zero height at startup.
            int target = numInterval.PreferredHeight;
            btnDateMode.Height = target;
            latestDateSelector.Height = target;
            btnSortMode.Height = target;
            btnUnit.Height = target;
            btnHelp.Width = target;
            btnHelp.Height = target;
            Button[] buttons = new Button[] { btnSort, btnFront, btnEnd, btnRemove, btnUndo, btnRedo, btnUndoDisk, btnRedoDisk };
            foreach (Button button in buttons)
            {
                button.Width = target;
                button.Height = target;
            }
        }

        private void ApplyLeftInsets()
        {
            int pixels = Math.Max(4, (int)Math.Round(Ui.InputLeftInset * uiScale));
            NativeMethods.SetEditLeftMargin(txtTemplate, pixels);
            NativeMethods.SetEditLeftMargin(txtExtension, pixels);
            foreach (Control child in numInterval.Controls)
            {
                TextBoxBase editor = child as TextBoxBase;
                if (editor != null) NativeMethods.SetEditLeftMargin(editor, pixels);
            }
        }

        private void WireEvents()
        {
            canvas.SelectionChanged += delegate { UpdateUiState(); };
            canvas.OrderChanged += delegate { UpdateUiState(); };
            canvas.ItemOpenRequested += delegate(int index) { OpenCanvasItem(index); };
            history.Changed += delegate { UpdateUiState(); };
            diskHistory.Changed += delegate { UpdateUiState(); };
            latestDateSelector.SelectionChanged += delegate { UpdateUiState(); SaveSettings(); };
            chkDate.CheckedChanged += delegate { UpdateUiState(); };
            chkExtension.CheckedChanged += delegate { UpdateUiState(); };
            chkRename.CheckedChanged += delegate { UpdateUiState(); };
            btnDateMode.Click += delegate { RefreshDateModeMenu(); dateModeMenu.Show(btnDateMode, new Point(0, btnDateMode.Height)); };
            btnUnit.Click += delegate { RefreshUnitMenu(); unitMenu.Show(btnUnit, new Point(0, btnUnit.Height)); };
            btnFront.Click += delegate { canvas.MoveSelected(true); };
            btnEnd.Click += delegate { canvas.MoveSelected(false); };
            btnRemove.Click += delegate { canvas.RemoveSelected(); };
            btnUndo.Click += delegate { history.Undo(); };
            btnRedo.Click += delegate { history.Redo(); };
            btnUndoDisk.Click += delegate { UndoDiskChange(); };
            btnRedoDisk.Click += delegate { RedoDiskChange(); };
            btnSortMode.Click += delegate { RefreshSortMenu(); sortMenu.Show(btnSortMode, new Point(0, btnSortMode.Height)); };
            btnSort.Click += delegate { SortItems(canvas.SelectedCount > 0); };
            txtTemplate.TextChanged += delegate
            {
                if (!ignoreTemplateChange) ClearIncrementRule();
            };
            txtExtension.TextChanged += delegate { UpdateUiState(); };
            btnSetIncrement.Click += delegate { SetIncrementRuleFromSelection(); };
            btnShortcutSettings.Click += delegate { ShowShortcutSettings(); };
            btnHelp.Click += delegate { ShowHelp(); };
            btnPreview.Click += delegate { ShowPreview(); };
            btnApply.Click += delegate { ApplyChanges(); };
            FormClosing += delegate { SaveSettings(); };
            FormClosed += delegate
            {
                Application.RemoveMessageFilter(this);
                disabledToolTipTimer.Stop();
                disabledToolTipTimer.Dispose();
            };
            Application.AddMessageFilter(this);
        }

        public bool PreFilterMessage(ref Message message)
        {
            const int WM_MOUSEMOVE = 0x0200;
            const int WM_NCMOUSEMOVE = 0x00A0;
            const int WM_MOUSEWHEEL = 0x020A;
            if ((message.Msg == WM_MOUSEMOVE || message.Msg == WM_NCMOUSEMOVE) && !IsDisposed && Visible)
            {
                UpdateDisabledControlToolTip(Control.MousePosition);
            }
            if (message.Msg != WM_MOUSEWHEEL || IsDisposed || !Visible) return false;
            int delta = unchecked((short)((long)message.WParam >> 16));
            if (delta == 0) return false;
            int step = delta > 0 ? -1 : 1;
            bool controlDown = (Control.ModifierKeys & Keys.Control) == Keys.Control;
            return HandleMouseWheelAt(Control.MousePosition, step, controlDown);
        }

        private void UpdateDisabledControlToolTip(Point screenPoint)
        {
            Control target = FindDisabledToolTipControl(this, screenPoint);
            if (target == disabledToolTipTarget) return;

            disabledToolTipTimer.Stop();
            if (disabledToolTipVisible)
            {
                toolTip.Hide(this);
                disabledToolTipVisible = false;
            }

            disabledToolTipTarget = target;
            if (target != null) disabledToolTipTimer.Start();
        }

        private Control FindDisabledToolTipControl(Control parent, Point screenPoint)
        {
            foreach (Control child in parent.Controls)
            {
                if (!child.Visible) continue;
                Rectangle bounds;
                try { bounds = child.RectangleToScreen(child.ClientRectangle); }
                catch { continue; }
                if (!bounds.Contains(screenPoint)) continue;

                Control nested = FindDisabledToolTipControl(child, screenPoint);
                if (nested != null) return nested;

                string text = toolTip.GetToolTip(child);
                if (!child.Enabled && !string.IsNullOrWhiteSpace(text)) return child;
            }
            return null;
        }

        private void ShowDisabledControlToolTip()
        {
            Control target = disabledToolTipTarget;
            if (target == null || target.IsDisposed || target.Enabled || !target.Visible) return;
            if (!PointerOver(target, Control.MousePosition)) return;

            string text = toolTip.GetToolTip(target);
            if (string.IsNullOrWhiteSpace(text)) return;
            Point location = PointToClient(Control.MousePosition);
            location.Offset(16, 20);
            toolTip.Show(text, this, location, toolTip.AutoPopDelay);
            disabledToolTipVisible = true;
        }

        internal bool HandleMouseWheelAt(Point screenPoint, int step, bool controlDown)
        {
            if (PointerOver(btnSortMode, screenPoint))
            {
                if (controlDown) CycleSortDirection(step); else CycleSort(step);
                FocusCanvasSoon();
                return true;
            }
            if (PointerOver(btnDateMode, screenPoint))
            {
                if (!chkDate.Checked) return true;
                if (controlDown) CycleDateDirection(step); else CycleDateType(step);
                FocusCanvasSoon();
                return true;
            }
            if (PointerOver(latestDateSelector, screenPoint))
            {
                if (!chkDate.Checked) return true;
                latestDateSelector.Cycle(step);
                FocusCanvasSoon();
                return true;
            }
            if (PointerOver(btnUnit, screenPoint))
            {
                if (!chkDate.Checked) return true;
                CycleUnit(step);
                FocusCanvasSoon();
                return true;
            }
            return false;
        }

        private bool PointerOver(Control control, Point screenPoint)
        {
            return control != null && control.Visible && control.RectangleToScreen(control.ClientRectangle).Contains(screenPoint);
        }

        private void CycleDateType(int step)
        {
            int current = changeCreationDate ? 1 : 0;
            SetDateType((current + step + 2) % 2);
        }

        private void CycleDateDirection(int step)
        {
            int current = timeDescending ? 1 : 0;
            SetDateDirection((current + step + 2) % 2);
        }

        private void CycleSortDirection(int step)
        {
            int current = Math.Max(0, cmbDirection.SelectedIndex);
            SetSortDirection((current + step + cmbDirection.Items.Count) % cmbDirection.Items.Count);
        }

        private void CycleUnit(int step)
        {
            int current = Math.Max(0, cmbUnit.SelectedIndex);
            SetUnit((current + step + cmbUnit.Items.Count) % cmbUnit.Items.Count);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys code = keyData & Keys.KeyCode;
            bool tabDown = (NativeMethods.GetAsyncKeyState((int)Keys.Tab) & 0x8000) != 0;

            if (code == Keys.Tab && shortcuts.Values.Any(delegate(ShortcutBinding b) { return b != null && b.TabPrefix; }))
                return true;

            string action = FindShortcutAction(keyData, tabDown);
            if (action != null)
            {
                if (IsEditingControlFocused() && !AllowedWhileEditing(action))
                    return base.ProcessCmdKey(ref msg, keyData);
                ExecuteShortcut(action);
                return true;
            }

            if (!IsEditingControlFocused() && keyData == (Keys.Control | Keys.A))
            {
                canvas.SelectAllItems();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private string FindShortcutAction(Keys keyData, bool tabDown)
        {
            foreach (string id in ShortcutIds.Order)
            {
                ShortcutBinding b;
                if (shortcuts.TryGetValue(id, out b) && b != null && b.Matches(keyData, tabDown)) return id;
            }
            return null;
        }

        private bool AllowedWhileEditing(string action)
        {
            return action == ShortcutIds.Preview || action == ShortcutIds.Apply || action == ShortcutIds.UndoDisk || action == ShortcutIds.RedoDisk;
        }

        private bool IsEditingControlFocused()
        {
            Control c = FindFocusedControl(this);
            return c is TextBoxBase || c is NumericUpDown || c is DateTimePicker;
        }

        private Control FindFocusedControl(Control root)
        {
            ContainerControl cc = root as ContainerControl;
            if (cc != null && cc.ActiveControl != null)
            {
                Control child = cc.ActiveControl;
                Control deeper = FindFocusedControl(child);
                return deeper ?? child;
            }
            foreach (Control c in root.Controls)
                if (c.Focused) return c;
            return root.Focused ? root : null;
        }

        private void ExecuteShortcut(string action)
        {
            if (action == ShortcutIds.Remove) canvas.RemoveSelected();
            else if (action == ShortcutIds.Sort) SortItems(canvas.SelectedCount > 0);
            else if (action == ShortcutIds.Undo) history.Undo();
            else if (action == ShortcutIds.Redo) history.Redo();
            else if (action == ShortcutIds.Preview) ShowPreview();
            else if (action == ShortcutIds.Apply) ApplyChanges();
            else if (action == ShortcutIds.UndoDisk) UndoDiskChange();
            else if (action == ShortcutIds.RedoDisk) RedoDiskChange();
            else if (action == ShortcutIds.MoveLeft) canvas.MoveSelectedOne(true);
            else if (action == ShortcutIds.MoveRight) canvas.MoveSelectedOne(false);
            else if (action == ShortcutIds.MoveFront) canvas.MoveSelected(true);
            else if (action == ShortcutIds.MoveEnd) canvas.MoveSelected(false);
            else if (action == ShortcutIds.PrevSort) CycleSort(-1);
            else if (action == ShortcutIds.NextSort) CycleSort(1);
            else if (action == ShortcutIds.PrevDirection) SetSortDirection(0);
            else if (action == ShortcutIds.NextDirection) SetSortDirection(1);
            else if (action == ShortcutIds.Help) ShowHelp();
        }

        private void CycleSort(int delta)
        {
            if (cmbSort.Items.Count == 0) return;
            int idx = cmbSort.SelectedIndex;
            if (idx < 0) idx = 0;
            idx = (idx + delta + cmbSort.Items.Count) % cmbSort.Items.Count;
            cmbSort.SelectedIndex = idx;
            RefreshSortMenu();
        }

        private void SetSortDirection(int index)
        {
            cmbDirection.SelectedIndex = Math.Max(0, Math.Min(1, index));
            RefreshSortMenu();
        }

        private void UpdateUiState()
        {
            int count = canvas.Items.Count;
            int selected = canvas.SelectedCount;
            bool dateEnabled = chkDate.Checked;
            lblOrder.Text = selected > 0 ? string.Format("已选：{0}个文件", selected) : string.Format("当前：{0}个文件", count);
            btnSort.Enabled = count > 1;
            btnFront.Enabled = selected > 0;
            btnEnd.Enabled = selected > 0;
            btnRemove.Enabled = selected > 0;
            btnUndo.Enabled = history.CanUndo;
            btnRedo.Enabled = history.CanRedo;
            btnUndoDisk.Enabled = diskHistory.CanUndo;
            btnRedoDisk.Enabled = diskHistory.CanRedo;
            btnDateMode.Inactive = false;
            btnDateMode.Enabled = dateEnabled;
            latestDateSelector.Inactive = false;
            latestDateSelector.Enabled = dateEnabled;
            numInterval.Enabled = dateEnabled;
            btnUnit.Inactive = false;
            btnUnit.Enabled = dateEnabled;
            lblDateModeTitle.ForeColor = SystemColors.ControlText;
            lblLatestDateTitle.ForeColor = SystemColors.ControlText;
            lblIntervalTitle.ForeColor = SystemColors.ControlText;
            txtExtension.Enabled = chkExtension.Checked;
            txtTemplate.Enabled = chkRename.Checked;
            btnSetIncrement.Enabled = chkRename.Checked;
            btnPreview.Enabled = count > 0;
            btnApply.Enabled = count > 0;
            RefreshDateModeMenu();
            RefreshUnitMenu();
            RefreshSortMenu();
            RefreshIncrementLabel();
            UpdateTooltips();
        }

        private void UpdateTooltips()
        {
            toolTip.SetToolTip(chkDate, UiText.DateFeatureTip);
            string disabledTip = UiText.DateFeatureDisabled;
            if (!chkDate.Checked)
            {
                toolTip.SetToolTip(lblDateModeTitle, disabledTip);
                toolTip.SetToolTip(btnDateMode, disabledTip);
                toolTip.SetToolTip(lblLatestDateTitle, disabledTip);
                toolTip.SetToolTip(latestDateSelector, disabledTip);
                toolTip.SetToolTip(lblIntervalTitle, disabledTip);
                toolTip.SetToolTip(numInterval, disabledTip);
                toolTip.SetToolTip(btnUnit, disabledTip);
            }
            else
            {
                toolTip.SetToolTip(lblDateModeTitle, "选择要更改的文件日期类型和日期顺序。");
                toolTip.SetToolTip(btnDateMode, "普通滚轮：切换修改日期/创建日期。\r\nCtrl+滚轮：切换升序/降序。\r\n" +
                    (timeDescending ? "当前降序：左上第一个文件日期最新。" : "当前升序：右下最后一个文件日期最新。"));
                toolTip.SetToolTip(lblLatestDateTitle, "本工具通过设置文件的“最晚日期”和日期的时间“间隔”来生成整组文件日期。\r\n这里设置的是整组文件中最晚日期，即最新的日期。");
                toolTip.SetToolTip(latestDateSelector, "文件的最晚日期：读取全部导入文件中所选日期类型的最新值。\r\n当前时间：点击预览或应用更改时的电脑本地时间。\r\n自定义：点击日期部分进行编辑，点击右侧箭头切换来源。\r\n滚轮：切换日期来源。");
                toolTip.SetToolTip(lblIntervalTitle, "设置相邻文件日期之间的间隔。");
                toolTip.SetToolTip(numInterval, "设置相邻文件日期之间的间隔数值。");
                toolTip.SetToolTip(btnUnit, "滚轮：切换秒/分。");
            }
            string renameScope = canvas.SelectedCount > 0 ? "选中文件" : "全部文件";
            toolTip.SetToolTip(chkExtension, "启用后把" + renameScope + "的扩展名统一改为输入值。可输入 jpg 或 .jpg。");
            toolTip.SetToolTip(txtExtension, "输入新的文件扩展名，例如 jpg 或 .jpg。当前范围：" + renameScope);
            toolTip.SetToolTip(chkRename, UiText.RenameFeatureTip);
            toolTip.SetToolTip(btnSetIncrement, "在名称中框选纯数字后设置递增区域。");
            SetShortcutTip(btnSort, "排序", ShortcutIds.Sort);
            SetShortcutTip(btnFront, "移到最前", ShortcutIds.MoveFront);
            SetShortcutTip(btnEnd, "移到最后", ShortcutIds.MoveEnd);
            SetShortcutTip(btnRemove, "移除（仅从列表移除，不删除磁盘文件）", ShortcutIds.Remove);
            SetShortcutTip(btnUndo, "撤销", ShortcutIds.Undo);
            SetShortcutTip(btnRedo, "重做", ShortcutIds.Redo);
            SetShortcutTip(btnUndoDisk, "撤销更改", ShortcutIds.UndoDisk);
            SetShortcutTip(btnRedoDisk, "重做更改", ShortcutIds.RedoDisk);
            SetShortcutTip(btnPreview, "预览", ShortcutIds.Preview);
            SetShortcutTip(btnApply, "应用更改", ShortcutIds.Apply);
            toolTip.SetToolTip(btnHelp, "使用说明 | 快捷键：F1");
            ShortcutBinding prev, next;
            string prevText = shortcuts.TryGetValue(ShortcutIds.PrevSort, out prev) && prev != null ? prev.Display() : "未设置";
            string nextText = shortcuts.TryGetValue(ShortcutIds.NextSort, out next) && next != null ? next.Display() : "未设置";
            ShortcutBinding prevDirection, nextDirection;
            string prevDirectionText = shortcuts.TryGetValue(ShortcutIds.PrevDirection, out prevDirection) && prevDirection != null ? prevDirection.Display() : "未设置";
            string nextDirectionText = shortcuts.TryGetValue(ShortcutIds.NextDirection, out nextDirection) && nextDirection != null ? nextDirection.Display() : "未设置";
            toolTip.SetToolTip(btnSortMode, "选择排序依据和方向。\r\n滚轮：切换排序依据。\r\nCtrl+滚轮：切换升序/降序。\r\n排序方式快捷键：" + prevText + " / " + nextText + "\r\n排序方向快捷键：" + prevDirectionText + " / " + nextDirectionText + "\r\n切换后需点击右侧“✓”键执行。");
        }

        private void SetShortcutTip(Control c, string actionName, string id)
        {
            ShortcutBinding b;
            string text = shortcuts.TryGetValue(id, out b) && b != null ? b.Display() : "未设置";
            toolTip.SetToolTip(c, actionName + " | 快捷键：" + text);
        }

        private void SortItems(bool selectedOnly)
        {
            if (canvas.Items.Count <= 1) return;
            List<FileItem> source;
            List<FileItem> rest;
            if (selectedOnly)
            {
                source = canvas.Items.Where(x => x.Selected).ToList();
                rest = canvas.Items.Where(x => !x.Selected).ToList();
                if (source.Count == 0) return;
            }
            else
            {
                source = new List<FileItem>(canvas.Items);
                rest = new List<FileItem>();
            }

            int key = cmbSort.SelectedIndex;
            bool desc = cmbDirection.SelectedIndex == 1;
            source.Sort(delegate(FileItem a, FileItem b)
            {
                int c = CompareItem(a, b, key);
                if (desc) c = -c;
                if (c == 0) c = NativeMethods.NaturalCompare(a.Name, b.Name);
                return c;
            });

            List<FileItem> after = new List<FileItem>();
            after.AddRange(source);
            after.AddRange(rest);
            canvas.ApplySortedOrder(after, selectedOnly ? "排序选中" : "排序全部");
        }

        private int CompareItem(FileItem a, FileItem b, int key)
        {
            if (key == 1) return DateTime.Compare(a.LastWriteTime, b.LastWriteTime);
            if (key == 2) return DateTime.Compare(a.CreationTime, b.CreationTime);
            if (key == 3) return a.Size.CompareTo(b.Size);
            if (key == 4)
            {
                int c = string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                return NativeMethods.NaturalCompare(a.Name, b.Name);
            }
            return NativeMethods.NaturalCompare(a.Name, b.Name);
        }

        private void SetIncrementRuleFromSelection()
        {
            string selected = txtTemplate.SelectedText;
            if (string.IsNullOrEmpty(selected))
            {
                MessageBox.Show(this, "请先在命名模板中用鼠标选中要递增的数字。", "设置递增编号", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            for (int i = 0; i < selected.Length; i++)
            {
                if (!char.IsDigit(selected[i]))
                {
                    MessageBox.Show(this, "递增区域必须是纯数字。", "设置递增编号", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            long v;
            if (!long.TryParse(selected, NumberStyles.None, CultureInfo.InvariantCulture, out v))
            {
                MessageBox.Show(this, "编号过大，无法处理。", "设置递增编号", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            renameIndex = txtTemplate.SelectionStart;
            renameLength = txtTemplate.SelectionLength;
            renameBase = v;
            chkRename.Checked = true;
            RefreshIncrementLabel();
        }

        private void ClearIncrementRule()
        {
            renameIndex = -1;
            renameLength = 0;
            renameBase = 0;
            RefreshIncrementLabel();
        }

        private void RefreshIncrementLabel()
        {
            string detail;
            if (renameIndex >= 0 && renameLength > 0 && renameIndex + renameLength <= txtTemplate.Text.Length)
            {
                string selected = txtTemplate.Text.Substring(renameIndex, renameLength);
                lblIncrement.Text = "已设置递增区域";
                detail = string.Format("已设置递增区域：{0}；起始编号：{1}；编号至少保留 {2} 位。", selected, renameBase, renameLength);
            }
            else
            {
                lblIncrement.Text = "未设置递增区域";
                detail = "尚未设置递增区域。请在名称中框选纯数字后点击“设置递增”。";
            }
            toolTip.SetToolTip(lblIncrement, detail);
        }

        private List<FileItem> RenameScope()
        {
            List<FileItem> selected = canvas.SelectedItemsInOrder;
            return selected.Count > 0 ? selected : new List<FileItem>(canvas.Items);
        }

        private string MakeRenameBaseName(int ordinal)
        {
            if (renameIndex < 0 || renameLength <= 0 || renameIndex + renameLength > txtTemplate.Text.Length) return null;
            string format = new string('0', renameLength);
            long number = checked(renameBase + ordinal);
            string n = number.ToString(format, CultureInfo.InvariantCulture);
            return txtTemplate.Text.Substring(0, renameIndex) + n + txtTemplate.Text.Substring(renameIndex + renameLength);
        }

        private bool ValidateRenameRule(out string error)
        {
            error = null;
            if (!chkRename.Checked) return true;
            if (string.IsNullOrWhiteSpace(txtTemplate.Text)) { error = "批量重命名已启用，但命名模板为空。"; return false; }
            if (renameIndex < 0 || renameLength <= 0) { error = "请先在命名模板中选中数字，并点击“设置递增”。"; return false; }
            if (renameIndex + renameLength > txtTemplate.Text.Length) { error = "命名模板已变化，请重新设置递增编号区域。"; return false; }
            try { MakeRenameBaseName(Math.Max(0, RenameScope().Count - 1)); }
            catch (OverflowException) { error = "递增编号超出可处理范围。"; return false; }
            return true;
        }

        private bool ValidateExtensionRule(out string extension, out string error)
        {
            extension = null;
            error = null;
            if (!chkExtension.Checked) return true;
            string value = (txtExtension.Text ?? "").Trim();
            while (value.StartsWith(".", StringComparison.Ordinal)) value = value.Substring(1);
            if (value.Length == 0) { error = "“更改扩展名”已启用，但扩展名输入框为空。"; return false; }
            if (value.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || value.IndexOf('\\') >= 0 || value.IndexOf('/') >= 0)
            { error = "文件扩展名包含 Windows 不允许的字符。"; return false; }
            if (value.EndsWith(" ", StringComparison.Ordinal) || value.EndsWith(".", StringComparison.Ordinal))
            { error = "文件扩展名不能以空格或句点结尾。"; return false; }
            extension = "." + value;
            return true;
        }

        private DateTime GetLatestDate()
        {
            if (latestDateSelector.SelectedMode == 1) return DateTime.Now;
            if (latestDateSelector.SelectedMode == 2) return latestDateSelector.CustomValue;
            DateTime latest = DateTime.MinValue;
            foreach (FileItem item in canvas.Items)
            {
                DateTime t = changeCreationDate ? item.CreationTime : item.LastWriteTime;
                if (t > latest) latest = t;
            }
            return latest == DateTime.MinValue ? DateTime.Now : latest;
        }

        private TimeSpan GetInterval()
        {
            double v = (double)numInterval.Value;
            return cmbUnit.SelectedIndex == 1 ? TimeSpan.FromMinutes(v) : TimeSpan.FromSeconds(v);
        }

        private bool BuildStates(out List<FileState> before, out List<FileState> after, out string error)
        {
            before = new List<FileState>();
            after = new List<FileState>();
            error = null;
            if (!chkDate.Checked && !chkExtension.Checked && !chkRename.Checked)
            { error = "请至少启用“更改文件日期”“更改扩展名”或“批量重命名”中的一项。"; return false; }
            if (!ValidateRenameRule(out error)) return false;
            string requestedExtension;
            if (!ValidateExtensionRule(out requestedExtension, out error)) return false;

            Dictionary<FileItem, string> renameTargets = new Dictionary<FileItem, string>();
            if (chkRename.Checked || chkExtension.Checked)
            {
                List<FileItem> scope = RenameScope();
                for (int i = 0; i < scope.Count; i++)
                {
                    string baseName = System.IO.Path.GetFileNameWithoutExtension(scope[i].Path);
                    if (chkRename.Checked)
                    {
                        try { baseName = MakeRenameBaseName(i); }
                        catch (OverflowException) { error = "递增编号超出可处理范围。"; return false; }
                        if (!ValidateBaseFileName(baseName, out error)) return false;
                    }
                    string ext = chkExtension.Checked ? requestedExtension : System.IO.Path.GetExtension(scope[i].Path);
                    string target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(scope[i].Path), baseName + ext);
                    renameTargets[scope[i]] = target;
                }
                if (!ValidateRenameTargets(renameTargets, out error)) return false;
            }

            DateTime latest = DateTime.Now;
            TimeSpan interval = TimeSpan.FromSeconds(1);
            if (chkDate.Checked) { latest = GetLatestDate(); interval = GetInterval(); }

            int total = canvas.Items.Count;
            for (int i = 0; i < total; i++)
            {
                FileItem item = canvas.Items[i];
                bool involved = chkDate.Checked || renameTargets.ContainsKey(item);
                if (!involved) continue;
                DateTime oldLastWrite;
                DateTime oldCreation;
                try
                {
                    oldLastWrite = File.GetLastWriteTime(item.Path);
                    oldCreation = File.GetCreationTime(item.Path);
                }
                catch (Exception ex) { error = "无法读取文件日期：\r\n" + item.Path + "\r\n" + ex.Message; return false; }
                string identity = FileIdentityHelper.TryGet(item.Path);
                bool changeLastWrite = chkDate.Checked && !changeCreationDate;
                bool changeCreation = chkDate.Checked && changeCreationDate;
                before.Add(new FileState { Item = item, Path = item.Path, LastWriteTime = oldLastWrite, CreationTime = oldCreation,
                    ChangeLastWriteTime = changeLastWrite, ChangeCreationTime = changeCreation, Identity = identity });
                string targetPath = renameTargets.ContainsKey(item) ? renameTargets[item] : item.Path;
                int distanceFromLatest = timeDescending ? i : total - 1 - i;
                DateTime targetDate = chkDate.Checked ? SafeSubtract(latest, interval, distanceFromLatest, out error) : oldLastWrite;
                if (error != null) return false;
                after.Add(new FileState { Item = item, Path = targetPath,
                    LastWriteTime = changeLastWrite ? targetDate : oldLastWrite,
                    CreationTime = changeCreation ? targetDate : oldCreation,
                    ChangeLastWriteTime = changeLastWrite, ChangeCreationTime = changeCreation, Identity = identity });
            }
            return true;
        }

        private DateTime SafeSubtract(DateTime latest, TimeSpan interval, int index, out string error)
        {
            error = null;
            try
            {
                long ticks = checked(interval.Ticks * (long)index);
                DateTime result = latest.Subtract(TimeSpan.FromTicks(ticks));
                if (result.Year < 1601) { error = "设置的间隔过大，最早文件的日期早于 Windows 文件日期可用范围。"; return latest; }
                return result;
            }
            catch
            {
                error = "设置的日期范围过大，请缩小间隔。";
                return latest;
            }
        }

        private bool ValidateBaseFileName(string name, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name)) { error = "生成的文件名为空。"; return false; }
            char[] invalid = System.IO.Path.GetInvalidFileNameChars();
            if (name.IndexOfAny(invalid) >= 0) { error = "命名模板包含 Windows 文件名不允许的字符。"; return false; }
            if (name.EndsWith(" ", StringComparison.Ordinal) || name.EndsWith(".", StringComparison.Ordinal)) { error = "Windows 文件名不能以空格或句点结尾。"; return false; }
            string stem = name;
            string[] reserved = new string[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            if (reserved.Any(delegate(string x) { return string.Equals(x, stem, StringComparison.OrdinalIgnoreCase); })) { error = "命名模板生成了 Windows 保留文件名：" + stem; return false; }
            return true;
        }

        private bool ValidateRenameTargets(Dictionary<FileItem, string> targets, out string error)
        {
            error = null;
            HashSet<string> sourcePaths = new HashSet<string>(targets.Keys.Select(delegate(FileItem x) { return Full(x.Path); }), StringComparer.OrdinalIgnoreCase);
            HashSet<string> targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<FileItem, string> kv in targets)
            {
                string target = Full(kv.Value);
                if (!targetPaths.Add(target)) { error = "重命名结果存在重复文件名：\r\n" + target; return false; }
                if (File.Exists(target) && !sourcePaths.Contains(target) && !string.Equals(Full(kv.Key.Path), target, StringComparison.OrdinalIgnoreCase))
                {
                    error = "目标文件名已存在，程序不会覆盖：\r\n" + target;
                    return false;
                }
            }
            return true;
        }

        private void OpenCanvasItem(int index)
        {
            if (index < 0 || index >= canvas.Items.Count) return;
            FileItem item = canvas.Items[index];
            if (ImageViewerForm.IsSupportedImage(item.Path))
            {
                List<FileItem> images = canvas.Items.Where(delegate(FileItem x) { return ImageViewerForm.IsSupportedImage(x.Path); }).ToList();
                int imageIndex = images.IndexOf(item);
                using (ImageViewerForm viewer = new ImageViewerForm(images, Math.Max(0, imageIndex))) viewer.ShowDialog(this);
                canvas.Focus();
                return;
            }
            try { Process.Start(item.Path); }
            catch (Exception ex) { MessageBox.Show(this, "无法打开文件：\r\n" + item.Path + "\r\n\r\n" + ex.Message, "打开文件失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void ShowPreview()
        {
            List<FileState> before, after;
            string error;
            if (!BuildStates(out before, out after, out error))
            {
                MessageBox.Show(this, error, "无法预览", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            FormWindowState ownerState = WindowState;
            using (PreviewForm f = new PreviewForm(before, after, chkDate.Checked, changeCreationDate, chkRename.Checked || chkExtension.Checked))
            {
                f.ShowDialog(this);
            }
            if (WindowState == FormWindowState.Minimized)
                WindowState = ownerState == FormWindowState.Minimized ? FormWindowState.Normal : ownerState;
            Activate();
            BringToFront();
            canvas.Focus();
        }

        private void ApplyChanges()
        {
            List<FileState> before, after;
            string error;
            if (!BuildStates(out before, out after, out error))
            {
                MessageBox.Show(this, error, "无法执行", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            int renameCount = 0;
            int extensionCount = 0;
            for (int i = 0; i < before.Count; i++)
            {
                if (!string.Equals(System.IO.Path.GetFileNameWithoutExtension(before[i].Path), System.IO.Path.GetFileNameWithoutExtension(after[i].Path), StringComparison.Ordinal)) renameCount++;
                if (!string.Equals(System.IO.Path.GetExtension(before[i].Path), System.IO.Path.GetExtension(after[i].Path), StringComparison.OrdinalIgnoreCase)) extensionCount++;
            }
            int dateCount = chkDate.Checked ? canvas.Items.Count : 0;
            string dateName = changeCreationDate ? "文件创建日期" : "文件修改日期";
            string msg = string.Format("即将处理 {0} 个文件。\r\n\r\n{1}：{2} 个\r\n重命名文件名：{3} 个\r\n重命名扩展名：{4} 个\r\n\r\n是否继续？",
                after.Count, dateName, dateCount, renameCount, extensionCount);
            if (!ConfirmForm.ShowConfirm(this, "确认应用更改", msg)) return;

            if (ApplyDiskStates(after))
            {
                List<FileState> actualAfter = CloneStates(after);
                RefreshStateSnapshots(actualAfter);
                diskHistory.Push(new DiskChangeAction(this, before, actualAfter));
                MessageBox.Show(this, "更改已完成。\r\n可使用“撤销更改”恢复本次磁盘修改。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void UndoDiskChange()
        {
            if (!diskHistory.CanUndo)
            {
                MessageBox.Show(this, "当前没有可撤销的文件更改。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string msg = "将撤销上一批已经写入磁盘的文件更改。\r\n执行前会检查文件是否被资源管理器或其他程序修改。\r\n\r\n是否继续？";
            if (!ConfirmForm.ShowConfirm(this, "确认撤销更改", msg)) return;
            diskHistory.Undo();
        }

        private void RedoDiskChange()
        {
            if (!diskHistory.CanRedo)
            {
                MessageBox.Show(this, "当前没有可重做的文件更改。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string msg = "将重新执行上一批已撤销的文件更改。\r\n执行前会检查文件是否被资源管理器或其他程序修改。\r\n\r\n是否继续？";
            if (!ConfirmForm.ShowConfirm(this, "确认重做更改", msg)) return;
            diskHistory.Redo();
        }

        public bool ApplyDiskTransition(List<FileState> expectedCurrent, List<FileState> targetStates)
        {
            string error;
            if (!ValidateExpectedDiskStates(expectedCurrent, out error))
            {
                MessageBox.Show(this, "检测到文件在上一次操作后发生了外部变化。为避免覆盖其他程序的修改，本次操作已停止。\r\n\r\n" + error,
                    "文件状态已变化", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (!ApplyDiskStates(targetStates)) return false;
            RefreshStateSnapshots(targetStates);
            return true;
        }

        private bool ValidateExpectedDiskStates(List<FileState> expectedStates, out string error)
        {
            error = null;
            foreach (FileState expected in expectedStates)
            {
                string found = ResolveCurrentPath(expected);
                if (found == null)
                {
                    error = "找不到原文件：\r\n" + expected.Path;
                    return false;
                }
                if (!SamePathAndCase(found, expected.Path))
                {
                    error = "文件名或位置已变化：\r\n预期：" + expected.Path + "\r\n当前：" + found;
                    return false;
                }
                string id = FileIdentityHelper.TryGet(found);
                if (!string.IsNullOrEmpty(expected.Identity) && !string.Equals(id, expected.Identity, StringComparison.Ordinal))
                {
                    error = "同一路径下的文件已不是原来的文件：\r\n" + found;
                    return false;
                }
                DateTime actualLastWrite;
                DateTime actualCreation;
                try
                {
                    actualLastWrite = File.GetLastWriteTime(found);
                    actualCreation = File.GetCreationTime(found);
                }
                catch (Exception ex) { error = "无法读取文件状态：\r\n" + found + "\r\n" + ex.Message; return false; }
                if (Math.Abs((actualLastWrite - expected.LastWriteTime).TotalMilliseconds) > 5.0)
                {
                    error = "文件修改日期已被外部改变：\r\n" + found + "\r\n预期：" + expected.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss.fff") + "\r\n当前：" + actualLastWrite.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    return false;
                }
                if (Math.Abs((actualCreation - expected.CreationTime).TotalMilliseconds) > 5.0)
                {
                    error = "文件创建日期已被外部改变：\r\n" + found + "\r\n预期：" + expected.CreationTime.ToString("yyyy-MM-dd HH:mm:ss.fff") + "\r\n当前：" + actualCreation.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    return false;
                }
                expected.Item.Path = found;
            }
            return true;
        }

        private string ResolveCurrentPath(FileState expected)
        {
            List<string> candidates = new List<string>();
            if (expected.Item != null && !string.IsNullOrEmpty(expected.Item.Path)) candidates.Add(expected.Item.Path);
            if (!string.IsNullOrEmpty(expected.Path)) candidates.Add(expected.Path);
            foreach (string p in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(p)) continue;
                if (string.IsNullOrEmpty(expected.Identity)) return p;
                string id = FileIdentityHelper.TryGet(p);
                if (string.Equals(id, expected.Identity, StringComparison.Ordinal)) return p;
                if (SamePathAndCase(p, expected.Path)) return p;
            }

            if (!string.IsNullOrEmpty(expected.Identity))
            {
                HashSet<string> dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try { if (!string.IsNullOrEmpty(expected.Path)) dirs.Add(System.IO.Path.GetDirectoryName(expected.Path)); } catch { }
                try { if (expected.Item != null && !string.IsNullOrEmpty(expected.Item.Path)) dirs.Add(System.IO.Path.GetDirectoryName(expected.Item.Path)); } catch { }
                foreach (string dir in dirs)
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    try
                    {
                        foreach (string file in Directory.GetFiles(dir))
                        {
                            string id = FileIdentityHelper.TryGet(file);
                            if (string.Equals(id, expected.Identity, StringComparison.Ordinal)) return file;
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        private bool SamePathAndCase(string a, string b)
        {
            string fa = Full(a);
            string fb = Full(b);
            if (!string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase)) return false;
            return string.Equals(System.IO.Path.GetFileName(fa), System.IO.Path.GetFileName(fb), StringComparison.Ordinal);
        }

        public bool ApplyDiskStates(List<FileState> targetStates)
        {
            if (targetStates == null || targetStates.Count == 0) return true;
            List<FileState> current = new List<FileState>();
            foreach (FileState target in targetStates)
            {
                string currentPath = target.Item.Path;
                if (!File.Exists(currentPath))
                {
                    MessageBox.Show(this, "找不到文件，操作已停止：\r\n" + currentPath, "文件更改失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
                current.Add(new FileState { Item = target.Item, Path = currentPath,
                    LastWriteTime = File.GetLastWriteTime(currentPath), CreationTime = File.GetCreationTime(currentPath),
                    ChangeLastWriteTime = target.ChangeLastWriteTime, ChangeCreationTime = target.ChangeCreationTime,
                    Identity = FileIdentityHelper.TryGet(currentPath) });
            }

            string error;
            if (!ValidateDiskTargetStates(targetStates, current, out error))
            {
                MessageBox.Show(this, error, "文件更改失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            Dictionary<FileItem, string> tempPaths = new Dictionary<FileItem, string>();
            try
            {
                foreach (FileState target in targetStates)
                {
                    string cur = target.Item.Path;
                    if (string.Equals(Full(cur), Full(target.Path), StringComparison.OrdinalIgnoreCase) && cur == target.Path) continue;
                    string dir = System.IO.Path.GetDirectoryName(cur);
                    string temp;
                    do { temp = System.IO.Path.Combine(dir, ".__FOTS_" + Guid.NewGuid().ToString("N") + ".tmp"); } while (File.Exists(temp));
                    File.Move(cur, temp);
                    tempPaths[target.Item] = temp;
                    target.Item.Path = temp;
                }

                foreach (FileState target in targetStates)
                {
                    if (tempPaths.ContainsKey(target.Item))
                    {
                        File.Move(tempPaths[target.Item], target.Path);
                        target.Item.Path = target.Path;
                    }
                }

                foreach (FileState target in targetStates)
                {
                    if (target.ChangeCreationTime) File.SetCreationTime(target.Item.Path, target.CreationTime);
                    if (target.ChangeLastWriteTime) File.SetLastWriteTime(target.Item.Path, target.LastWriteTime);
                }

                canvas.Invalidate();
                UpdateUiState();
                return true;
            }
            catch (Exception ex)
            {
                try { RollbackCurrentStates(current); } catch { }
                MessageBox.Show(this, "操作过程中出现错误，程序已尽量恢复原状态。\r\n\r\n" + ex.Message, "文件更改失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private bool ValidateDiskTargetStates(List<FileState> targets, List<FileState> current, out string error)
        {
            error = null;
            HashSet<string> currentPaths = new HashSet<string>(current.Select(delegate(FileState x) { return Full(x.Path); }), StringComparer.OrdinalIgnoreCase);
            HashSet<string> finalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileState t in targets)
            {
                string p = Full(t.Path);
                if (!finalPaths.Add(p)) { error = "操作后的文件名发生冲突：\r\n" + p; return false; }
                if (File.Exists(p) && !currentPaths.Contains(p)) { error = "目标位置已有文件，程序不会覆盖：\r\n" + p; return false; }
            }
            return true;
        }

        private void RollbackCurrentStates(List<FileState> states)
        {
            Dictionary<FileItem, string> temps = new Dictionary<FileItem, string>();
            foreach (FileState s in states)
            {
                string cur = s.Item.Path;
                if (!File.Exists(cur)) continue;
                if (string.Equals(Full(cur), Full(s.Path), StringComparison.OrdinalIgnoreCase) && cur == s.Path) continue;
                string dir = System.IO.Path.GetDirectoryName(cur);
                string temp = System.IO.Path.Combine(dir, ".__FOTS_RB_" + Guid.NewGuid().ToString("N") + ".tmp");
                File.Move(cur, temp);
                temps[s.Item] = temp;
                s.Item.Path = temp;
            }
            foreach (FileState s in states)
            {
                if (temps.ContainsKey(s.Item))
                {
                    File.Move(temps[s.Item], s.Path);
                    s.Item.Path = s.Path;
                }
                if (File.Exists(s.Item.Path))
                {
                    if (s.ChangeCreationTime) File.SetCreationTime(s.Item.Path, s.CreationTime);
                    if (s.ChangeLastWriteTime) File.SetLastWriteTime(s.Item.Path, s.LastWriteTime);
                }
            }
            canvas.Invalidate();
            UpdateUiState();
        }

        private List<FileState> CloneStates(List<FileState> states)
        {
            List<FileState> copy = new List<FileState>();
            foreach (FileState s in states)
                copy.Add(new FileState { Item = s.Item, Path = s.Path, LastWriteTime = s.LastWriteTime,
                    CreationTime = s.CreationTime, ChangeLastWriteTime = s.ChangeLastWriteTime,
                    ChangeCreationTime = s.ChangeCreationTime, Identity = s.Identity });
            return copy;
        }

        private void RefreshStateSnapshots(List<FileState> states)
        {
            foreach (FileState s in states)
            {
                if (s.Item == null || !File.Exists(s.Item.Path)) continue;
                s.Path = s.Item.Path;
                try { s.LastWriteTime = File.GetLastWriteTime(s.Item.Path); } catch { }
                try { s.CreationTime = File.GetCreationTime(s.Item.Path); } catch { }
                s.Identity = FileIdentityHelper.TryGet(s.Item.Path);
            }
        }

        private void ShowShortcutSettings()
        {
            ShortcutSettingsForm f = new ShortcutSettingsForm(shortcuts);
            if (f.ShowDialog(this) == DialogResult.OK)
            {
                shortcuts = CloneShortcuts(f.Bindings);
                UpdateTooltips();
                SaveSettings();
            }
        }

        private void ShowHelp()
        {
            using (HelpForm f = new HelpForm()) f.ShowDialog(this);
            canvas.Focus();
        }

        private Dictionary<string, ShortcutBinding> CloneShortcuts(Dictionary<string, ShortcutBinding> source)
        {
            Dictionary<string, ShortcutBinding> d = new Dictionary<string, ShortcutBinding>();
            foreach (KeyValuePair<string, ShortcutBinding> kv in source) d[kv.Key] = kv.Value == null ? null : kv.Value.Clone();
            return d;
        }

        private void LoadSettings()
        {
            latestDateSelector.SelectedMode = 0;
            numInterval.Value = 1;
            cmbUnit.SelectedIndex = 0;
            cmbSort.SelectedIndex = 0;
            cmbDirection.SelectedIndex = 0;
            latestDateSelector.CustomValue = DateTime.Now;
            timeDescending = true;
            changeCreationDate = false;
            helpShown = false;
            shortcuts = ShortcutIds.Defaults();
            try
            {
                if (!File.Exists(SettingsPath)) return;
                Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                int start;
                decimal interval;
                int unit;
                int desc;
                int windowWidth, windowHeight, windowMaximized, creationMode, helpWasShown;
                if (d.ContainsKey("StartMode") && int.TryParse(d["StartMode"], out start)) latestDateSelector.SelectedMode = Math.Max(0, Math.Min(2, start));
                if (d.ContainsKey("Interval") && decimal.TryParse(d["Interval"], NumberStyles.Number, CultureInfo.InvariantCulture, out interval)) numInterval.Value = Math.Max(numInterval.Minimum, Math.Min(numInterval.Maximum, interval));
                if (d.ContainsKey("Unit") && int.TryParse(d["Unit"], out unit)) cmbUnit.SelectedIndex = Math.Max(0, Math.Min(1, unit));
                if (d.ContainsKey("TimeDescending") && int.TryParse(d["TimeDescending"], out desc)) timeDescending = desc != 0;
                if (d.ContainsKey("ChangeCreationDate") && int.TryParse(d["ChangeCreationDate"], out creationMode)) changeCreationDate = creationMode != 0;
                if (d.ContainsKey("HelpShown") && int.TryParse(d["HelpShown"], out helpWasShown)) helpShown = helpWasShown != 0;
                if (d.ContainsKey("WindowWidth") && d.ContainsKey("WindowHeight") &&
                    int.TryParse(d["WindowWidth"], out windowWidth) && int.TryParse(d["WindowHeight"], out windowHeight))
                {
                    int physicalWidth = Math.Max(640, (int)Math.Round(windowWidth * uiScale));
                    int physicalHeight = Math.Max(420, (int)Math.Round(windowHeight * uiScale));
                    Rectangle work = Screen.PrimaryScreen.WorkingArea;
                    Size = new Size(Math.Min(physicalWidth, work.Width), Math.Min(physicalHeight, work.Height));
                }
                if (d.ContainsKey("WindowMaximized") && int.TryParse(d["WindowMaximized"], out windowMaximized))
                    restoreMaximized = windowMaximized != 0;
                foreach (string id in ShortcutIds.Order)
                {
                    string key = "Shortcut_" + id;
                    if (!d.ContainsKey(key)) continue;
                    ShortcutBinding b = ShortcutBinding.Parse(d[key]);
                    if (b != null) shortcuts[id] = b;
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                List<string> lines = new List<string>();
                lines.Add("StartMode=" + latestDateSelector.SelectedMode.ToString(CultureInfo.InvariantCulture));
                lines.Add("Interval=" + numInterval.Value.ToString(CultureInfo.InvariantCulture));
                lines.Add("Unit=" + cmbUnit.SelectedIndex.ToString(CultureInfo.InvariantCulture));
                lines.Add("TimeDescending=" + (timeDescending ? "1" : "0"));
                lines.Add("ChangeCreationDate=" + (changeCreationDate ? "1" : "0"));
                lines.Add("HelpShown=" + (helpShown ? "1" : "0"));
                Size rememberedSize = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
                lines.Add("WindowWidth=" + Math.Max(1, (int)Math.Round(rememberedSize.Width / uiScale)).ToString(CultureInfo.InvariantCulture));
                lines.Add("WindowHeight=" + Math.Max(1, (int)Math.Round(rememberedSize.Height / uiScale)).ToString(CultureInfo.InvariantCulture));
                lines.Add("WindowMaximized=" + (WindowState == FormWindowState.Maximized ? "1" : "0"));
                foreach (string id in ShortcutIds.Order)
                {
                    ShortcutBinding b;
                    if (shortcuts.TryGetValue(id, out b) && b != null) lines.Add("Shortcut_" + id + "=" + b.Serialize());
                }
                File.WriteAllLines(SettingsPath, lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        private static string Full(string p)
        {
            try { return System.IO.Path.GetFullPath(p); } catch { return p; }
        }
    }

    sealed class HelpForm : Form
    {
        private readonly ListBox topics = new ListBox();
        private readonly TextBox content = new TextBox();
        private readonly string[] titles = new string[]
        {
            "常见问题", "快速开始", "导入与图片查看", "选择与排列", "排序",
            "更改文件日期", "文件重命名", "预览与应用", "撤销与重做", "快捷键与运行"
        };
        private readonly string[] bodies = new string[]
        {
            UiText.HelpApplicationQuestion + "\r\n\r\n" + UiText.HelpFirstQuestion + "\r\n\r\n问：什么是“最晚日期”和“间隔”？\r\n答：本工具先确定整组文件中最新的日期，再按照间隔逐个向前计算其他文件的日期。\r\n\r\n问：“文件的最晚日期”读取哪一种日期？\r\n答：“修改模式：修改日期”时读取全部导入文件中最新的修改日期；切换到创建日期时读取最新的创建日期。\r\n\r\n问：“当前时间”是什么？\r\n答：它是点击“预览”或“应用更改”时电脑显示的本地时间。预览与实际应用之间如果间隔较久，应用时会重新读取当前时间。\r\n\r\n问：选择文件会影响哪些操作？\r\n答：“更改文件日期”始终处理全部导入文件。文件名和扩展名重命名在有选择时只处理选中文件，没有选择时处理全部文件。",
            "1. 把文件从资源管理器直接拖入主窗口。\r\n\r\n2. 使用排序、移动按钮或拖动卡片调整视觉顺序。\r\n\r\n3. 勾选需要的功能：更改文件日期、更改扩展名或批量重命名。\r\n\r\n4. 更改日期时选择修改模式、最晚日期来源和间隔。\r\n\r\n5. 先点“预览”核对，再点“应用更改”。",
            "只接受文件；文件夹会被忽略，重复文件不会再次加入。图片和视频缩略图由 Windows 提供。\r\n\r\n双击图片会在程序内部打开大图。使用 ← / → 或窗口底部按钮，按照主界面当前视觉顺序切换图片；视频和其他文件仍由系统默认程序打开。\r\n\r\n文件名最多显示两行，悬停卡片可以查看完整文件名。Delete 只从工具列表移除，不删除磁盘文件。",
            "单击：只选一个。\r\nCtrl+单击：追加或取消选择。\r\nShift+单击：连续选择。\r\n空白处拖动：框选。\r\nCtrl+A：全选。\r\n右键缩略图区：取消全部选择。\r\n\r\n拖动一个或多个已选文件时，蓝色插入线表示整组最终位置。“移到最前”和“移到最后”会保留选中组内部的顺序。",
            "排序框只负责选择排序依据和升降序，不会持续限制文件顺序。选择后需要点击右侧“✓”执行一次排序；排序完成后，仍可继续拖动或使用移动按钮自定义排列。\r\n\r\n排序菜单的分隔线上方是文件名、修改日期、创建日期、文件大小和文件类型；下方是升序和降序。圆点表示当前依据和方向。\r\n\r\n鼠标位于排序框时，滚轮切换排序依据，Ctrl+滚轮切换升降序。使用快捷键 Tab+↑/↓ 可直接切换排序依据，Ctrl+Tab+↑/↓ 可直接切换升降序。\r\n\r\n有选择时，排序会整理选中文件并把它们聚到最前；没有选择时处理全部文件。第二排其他图标依次为移到最前、移到最后、移除、撤销和重做；最右侧两个带文件标记的图标用于撤销更改和重做更改。",
            "“修改模式：修改日期/创建日期”决定要写入哪一种 Windows 文件日期，升序/降序决定日期排列方向。“更改文件日期”始终处理全部导入文件。\r\n\r\n鼠标位于修改模式框时，滚轮切换日期类型，Ctrl+滚轮切换升降序。降序表示左上第一个文件日期最新，升序表示右下最后一个文件日期最新。\r\n\r\n最晚日期来源包括“文件的最晚日期”“当前时间”和“自定义”，可用滚轮切换。自定义状态点击日期部分可编辑，点击右侧箭头可打开菜单。",
            "扩展名：勾选“更改扩展名”，输入 jpg 或 .jpg 均可。扩展名输入框为空时不会执行，也不会删除原扩展名。\r\n\r\n文件名：勾选“批量重命名”，输入名称，框选其中一段纯数字，再点“设置递增”。例如框选“旅行照片1”中的“1”，会生成旅行照片1至旅行照片9，然后生成旅行照片10、旅行照片11……；框选“旅行照片01”中的“01”，则会生成旅行照片01至旅行照片09，然后生成旅行照片10、旅行照片11……。原数字位数只是编号的最少位数，超过该位数时会自然增加，不会截断。\r\n\r\n两项可以单独或同时使用。程序会在执行前检查重名和已有文件，绝不会覆盖。",
            "“预览”只显示计划结果，不会写入文件。表格会显示最终文件名以及所选的修改日期或创建日期。\r\n\r\n“应用更改”可以一次完成日期、文件名和扩展名操作。确认窗口默认选中“确定”，直接按 Enter 可以继续。\r\n\r\n执行前会检查路径、文件标识、修改日期、创建日期和目标名称；发现外部变化或冲突时停止。",
            "“撤销/重做”只处理主界面中的排列操作。\r\n\r\n“撤销更改/重做更改”处理已经写入磁盘的日期、文件名和扩展名，并在本次运行期间保留多级历史。新的磁盘操作会清空磁盘重做记录。\r\n\r\n如果文件在程序外被移动、改名或改变日期，安全检查会停止操作。",
            "Delete：移除选中\r\nEnter：排序选中/全部\r\nCtrl+Z：撤销界面\r\nCtrl+Shift+Z：重做界面\r\nShift+Enter：应用更改\r\nAlt+Enter：预览\r\nCtrl+Alt+Z：撤销磁盘更改\r\nCtrl+Shift+Alt+Z：重做磁盘更改\r\n← / →：整组选中文件向前/后移动\r\n↑ / ↓：移到最前/最后\r\nTab+↑ / ↓：切换排序依据\r\nCtrl+Tab+↑ / ↓：切换升序/降序\r\nF1：使用说明\r\n\r\n程序首次运行时自动打开本说明，以后可用 F1 或“？”再次打开。程序只允许一个实例，并会记住窗口尺寸而不记住窗口位置。"
        };

        public HelpForm()
        {
            Text = "使用说明 - 批量文件排序/重命名工具 v1.8.4.5 测试版";
            ClientSize = new Size(760, 540);
            MinimumSize = new Size(620, 440);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(12);
            root.ColumnCount = 2;
            root.RowCount = 2;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(root);

            topics.Dock = DockStyle.Fill;
            topics.IntegralHeight = false;
            topics.Margin = new Padding(0, 0, 10, 0);
            topics.Items.AddRange(titles);
            root.Controls.Add(topics, 0, 0);

            content.Dock = DockStyle.Fill;
            content.Multiline = true;
            content.ReadOnly = true;
            content.ScrollBars = ScrollBars.Vertical;
            content.BackColor = Color.White;
            content.Margin = new Padding(0);
            root.Controls.Add(content, 1, 0);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            buttons.Margin = new Padding(0);
            buttons.Padding = new Padding(0, 7, 0, 0);
            Button close = new Button { Text = "关闭", DialogResult = DialogResult.OK, Width = 88, Height = 28 };
            buttons.Controls.Add(close);
            root.SetColumnSpan(buttons, 2);
            root.Controls.Add(buttons, 0, 1);
            AcceptButton = close;
            CancelButton = close;

            topics.SelectedIndexChanged += delegate
            {
                if (topics.SelectedIndex >= 0) content.Text = bodies[topics.SelectedIndex];
            };
            topics.SelectedIndex = 0;
            Ui.ScaleFormOnce(this);
        }
    }

    sealed class ImageViewerForm : Form
    {
        private readonly List<FileItem> images;
        private int index;
        private readonly PictureBox picture = new PictureBox();
        private readonly Label status = new Label();
        private readonly Label error = new Label();
        private readonly Button previous = new Button();
        private readonly Button next = new Button();
        private Image loadedImage;

        public ImageViewerForm(List<FileItem> source, int startIndex)
        {
            images = source ?? new List<FileItem>();
            index = Math.Max(0, Math.Min(Math.Max(0, images.Count - 1), startIndex));
            Text = "图片查看";
            ClientSize = new Size(1000, 700);
            MinimumSize = new Size(640, 460);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            ShowInTaskbar = false;

            Panel imageArea = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(28, 28, 28) };
            picture.Dock = DockStyle.Fill;
            picture.SizeMode = PictureBoxSizeMode.Zoom;
            picture.BackColor = imageArea.BackColor;
            imageArea.Controls.Add(picture);
            error.Dock = DockStyle.Fill;
            error.TextAlign = ContentAlignment.MiddleCenter;
            error.ForeColor = Color.WhiteSmoke;
            error.BackColor = imageArea.BackColor;
            error.Visible = false;
            imageArea.Controls.Add(error);
            Controls.Add(imageArea);

            TableLayoutPanel bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 52;
            bottom.Padding = new Padding(12, 8, 12, 8);
            bottom.ColumnCount = 3;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            bottom.BackColor = Color.FromArgb(245, 245, 245);
            previous.Text = "←";
            previous.Dock = DockStyle.Fill;
            previous.Margin = new Padding(0);
            next.Text = "→";
            next.Dock = DockStyle.Fill;
            next.Margin = new Padding(0);
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleCenter;
            status.AutoEllipsis = true;
            status.Margin = new Padding(12, 0, 12, 0);
            bottom.Controls.Add(previous, 0, 0);
            bottom.Controls.Add(status, 1, 0);
            bottom.Controls.Add(next, 2, 0);
            Controls.Add(bottom);

            previous.Click += delegate { Navigate(-1); };
            next.Click += delegate { Navigate(1); };
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Left) { Navigate(-1); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Right) { Navigate(1); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { Close(); e.SuppressKeyPress = true; }
            };
            FormClosed += delegate { DisposeLoadedImage(); };
            Ui.ScaleFormOnce(this);
            Shown += delegate { ShowCurrent(); };
        }

        public static bool IsSupportedImage(string path)
        {
            string ext;
            try { ext = System.IO.Path.GetExtension(path).ToLowerInvariant(); }
            catch { return false; }
            return new string[] { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".avif" }.Contains(ext);
        }

        private void Navigate(int delta)
        {
            int nextIndex = Math.Max(0, Math.Min(images.Count - 1, index + delta));
            if (nextIndex == index) return;
            index = nextIndex;
            ShowCurrent();
        }

        private void ShowCurrent()
        {
            DisposeLoadedImage();
            if (images.Count == 0) { error.Text = "没有可预览的图片。"; error.Visible = true; return; }
            FileItem item = images[index];
            string name = System.IO.Path.GetFileName(item.Path);
            status.Text = string.Format("{0} / {1}    {2}", index + 1, images.Count, name);
            Text = "图片查看 - " + name;
            previous.Enabled = index > 0;
            next.Enabled = index < images.Count - 1;
            try
            {
                loadedImage = LoadUnlocked(item.Path);
                if (loadedImage == null) loadedImage = ShellThumbnail.GetThumbnail(item.Path, new Size(1600, 1200));
                if (loadedImage == null) throw new InvalidOperationException("Windows 无法生成这张图片的预览。");
                picture.Image = loadedImage;
                error.Visible = false;
                picture.Visible = true;
            }
            catch (Exception ex)
            {
                picture.Visible = false;
                error.Text = "无法预览此图片\r\n\r\n" + ex.Message;
                error.Visible = true;
                error.BringToFront();
            }
        }

        private static Image LoadUnlocked(string path)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (Image source = Image.FromStream(stream, true, true))
                    return new Bitmap(source);
            }
            catch { return null; }
        }

        private void DisposeLoadedImage()
        {
            picture.Image = null;
            if (loadedImage != null) { loadedImage.Dispose(); loadedImage = null; }
        }
    }

    sealed class ConfirmForm : Form
    {
        private readonly Button yes = new Button();
        private readonly Button no = new Button();

        private ConfirmForm(string title, string message)
        {
            Text = title;
            ClientSize = new Size(470, 210);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(18, 16, 14, 12);
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(root);

            Label lbl = new Label();
            lbl.Text = message;
            lbl.Dock = DockStyle.Fill;
            lbl.Margin = new Padding(0, 0, 0, 8);
            lbl.TextAlign = ContentAlignment.TopLeft;
            root.Controls.Add(lbl, 0, 0);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(0, 5, 0, 0);
            buttons.Margin = new Padding(0);
            yes.Text = "确定(S)";
            no.Text = "取消(N)";
            yes.Width = no.Width = 92;
            yes.Height = no.Height = 28;
            yes.DialogResult = DialogResult.Yes;
            no.DialogResult = DialogResult.No;
            yes.TabIndex = 0;
            no.TabIndex = 1;
            buttons.Controls.Add(no);
            buttons.Controls.Add(yes);
            root.Controls.Add(buttons, 0, 1);

            int measured = TextRenderer.MeasureText(message, Font, new Size(430, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
            ClientSize = new Size(470, Math.Max(190, measured + 92));
            Ui.ScaleFormOnce(this);

            AcceptButton = yes;
            CancelButton = no;
            ActiveControl = yes;
            Shown += delegate
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    yes.Select();
                    yes.Focus();
                });
            };
            KeyDown += ConfirmForm_KeyDown;
        }

        private void ConfirmForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (!e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.S)
            {
                DialogResult = DialogResult.Yes;
                Close();
                e.SuppressKeyPress = true;
            }
            else if (!e.Control && !e.Alt && !e.Shift && (e.KeyCode == Keys.N || e.KeyCode == Keys.Escape))
            {
                DialogResult = DialogResult.No;
                Close();
                e.SuppressKeyPress = true;
            }
        }

        public static bool ShowConfirm(IWin32Window owner, string title, string message)
        {
            using (ConfirmForm f = new ConfirmForm(title, message)) return f.ShowDialog(owner) == DialogResult.Yes;
        }
    }

    sealed class ShortcutSettingsForm : Form
    {
        private readonly ListView list = new ListView();
        private readonly Button btnChange = new Button();
        private readonly Button btnDefault = new Button();
        public Dictionary<string, ShortcutBinding> Bindings { get; private set; }

        public ShortcutSettingsForm(Dictionary<string, ShortcutBinding> source)
        {
            Bindings = Clone(source);
            Text = "快捷键设置";
            Width = 540;
            Height = 520;
            MinimumSize = new Size(480, 420);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.None;

            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.MultiSelect = false;
            list.GridLines = true;
            list.HideSelection = false;
            list.Columns.Add("功能", 270);
            list.Columns.Add("快捷键", 190);
            list.DoubleClick += delegate { ChangeSelected(); };
            Controls.Add(list);

            FlowLayoutPanel bottom = new FlowLayoutPanel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 52;
            bottom.Padding = new Padding(8);
            btnChange.Text = "修改快捷键";
            btnDefault.Text = "恢复默认";
            Button ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 85 };
            Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 85 };
            btnChange.Width = 100;
            btnDefault.Width = 90;
            bottom.Controls.Add(btnChange);
            bottom.Controls.Add(btnDefault);
            bottom.Controls.Add(ok);
            bottom.Controls.Add(cancel);
            Controls.Add(bottom);
            AcceptButton = ok;
            CancelButton = cancel;

            btnChange.Click += delegate { ChangeSelected(); };
            btnDefault.Click += delegate { Bindings = ShortcutIds.Defaults(); RefreshRows(); };
            RefreshRows();
            Ui.ScaleFormOnce(this);
        }

        private void RefreshRows()
        {
            list.Items.Clear();
            foreach (string id in ShortcutIds.Order)
            {
                ShortcutBinding b;
                Bindings.TryGetValue(id, out b);
                ListViewItem item = new ListViewItem(ShortcutIds.Title(id));
                item.Name = id;
                item.Tag = id;
                item.SubItems.Add(b == null ? "未设置" : b.Display());
                list.Items.Add(item);
            }
            if (list.Items.Count > 0 && list.SelectedItems.Count == 0) list.Items[0].Selected = true;
        }

        private void ChangeSelected()
        {
            if (list.SelectedItems.Count == 0) return;
            string id = list.SelectedItems[0].Tag as string;
            ShortcutCaptureForm f = new ShortcutCaptureForm(ShortcutIds.Title(id));
            if (f.ShowDialog(this) != DialogResult.OK || f.Binding == null) return;
            foreach (KeyValuePair<string, ShortcutBinding> kv in Bindings)
            {
                if (kv.Key == id || kv.Value == null) continue;
                if (kv.Value.Equals(f.Binding))
                {
                    MessageBox.Show(this, "这个快捷键已经用于“" + ShortcutIds.Title(kv.Key) + "”，请换一个。", "快捷键重复", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            Bindings[id] = f.Binding;
            RefreshRows();
            foreach (ListViewItem item in list.Items)
                if ((string)item.Tag == id) { item.Selected = true; item.Focused = true; item.EnsureVisible(); break; }
        }

        private static Dictionary<string, ShortcutBinding> Clone(Dictionary<string, ShortcutBinding> source)
        {
            Dictionary<string, ShortcutBinding> d = new Dictionary<string, ShortcutBinding>();
            foreach (KeyValuePair<string, ShortcutBinding> kv in source) d[kv.Key] = kv.Value == null ? null : kv.Value.Clone();
            return d;
        }
    }

    sealed class ShortcutCaptureForm : Form
    {
        private readonly Label value = new Label();
        public ShortcutBinding Binding { get; private set; }

        public ShortcutCaptureForm(string actionName)
        {
            Text = "设置快捷键 - " + actionName;
            Width = 410;
            Height = 190;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            KeyPreview = true;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            Label tip = new Label();
            tip.Text = "请直接按下新的快捷键组合。\r\nTab+方向键也可以直接按住 Tab 后再按方向键。\r\n按 Esc 取消。";
            tip.Dock = DockStyle.Fill;
            tip.Padding = new Padding(14, 12, 14, 0);
            root.Controls.Add(tip, 0, 0);

            value.Text = "等待输入…";
            value.Dock = DockStyle.Fill;
            value.TextAlign = ContentAlignment.MiddleCenter;
            value.Font = new Font("Segoe UI", 14f, FontStyle.Bold);
            root.Controls.Add(value, 0, 1);
            Ui.ScaleFormOnce(this);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys code = keyData & Keys.KeyCode;
            Keys mods = keyData & Keys.Modifiers;
            if (code == Keys.Escape && mods == Keys.None)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return true;
            }
            if (code == Keys.Tab) return true;
            if (code == Keys.ControlKey || code == Keys.ShiftKey || code == Keys.Menu) return true;
            if (code == Keys.F4 && (mods & Keys.Alt) == Keys.Alt) return base.ProcessCmdKey(ref msg, keyData);

            bool tabDown = (NativeMethods.GetAsyncKeyState((int)Keys.Tab) & 0x8000) != 0;
            Binding = new ShortcutBinding { KeyCode = code, Modifiers = mods, TabPrefix = tabDown };
            value.Text = Binding.Display();
            DialogResult = DialogResult.OK;
            Close();
            return true;
        }
    }

    sealed class PreviewForm : Form
    {
        public PreviewForm(List<FileState> before, List<FileState> after, bool showDate, bool creationDate, bool showRename)
        {
            Text = "更改预览";
            ClientSize = new Size(920, 580);
            MinimumSize = new Size(720, 420);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.None;
            MinimizeBox = false;
            ShowInTaskbar = false;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(8, 0, 8, 8);
            root.ColumnCount = 1;
            root.RowCount = 2;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            Label tip = new Label();
            tip.Dock = DockStyle.Fill;
            tip.Margin = new Padding(0);
            tip.Padding = new Padding(0, 5, 0, 5);
            tip.AutoSize = true;
            tip.TextAlign = ContentAlignment.MiddleLeft;
            tip.Text = "此窗口仅预览，不会修改文件。关闭后点击“应用更改”才会写入。";
            root.Controls.Add(tip, 0, 0);

            DataGridView grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.Margin = new Padding(0);
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.RowHeadersVisible = false;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 32;
            grid.RowTemplate.Height = 26;
            grid.Columns.Add("Order", "顺序");
            grid.Columns.Add("Old", "当前文件名");
            grid.Columns.Add("New", "新文件名");
            grid.Columns.Add("Time", creationDate ? "新创建日期" : "新修改日期");
            grid.Columns[0].FillWeight = 12;
            grid.Columns[1].FillWeight = 30;
            grid.Columns[2].FillWeight = 30;
            grid.Columns[3].FillWeight = 28;
            for (int i = 0; i < after.Count; i++)
            {
                string oldName = System.IO.Path.GetFileName(before[i].Path);
                string newName = System.IO.Path.GetFileName(after[i].Path);
                DateTime targetDate = creationDate ? after[i].CreationTime : after[i].LastWriteTime;
                grid.Rows.Add((i + 1).ToString(), oldName, newName, showDate ? targetDate.ToString("yyyy-MM-dd HH:mm:ss") : "—");
            }
            root.Controls.Add(grid, 0, 1);
            Ui.ScaleFormOnce(this);
        }
    }

    static class NativeMethods
    {
        private const int SW_RESTORE = 9;
        private const int EM_SETMARGINS = 0x00D3;
        private const int EC_LEFTMARGIN = 0x0001;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public static void SetEditLeftMargin(TextBoxBase textBox, int pixels)
        {
            if (textBox == null || textBox.IsDisposed) return;
            int value = pixels & 0xFFFF;
            SendMessage(textBox.Handle, EM_SETMARGINS, (IntPtr)EC_LEFTMARGIN, (IntPtr)value);
        }

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        public static void ActivateExistingInstance(string title, string version)
        {
            IntPtr window = IntPtr.Zero;
            for (int attempt = 0; attempt < 20 && window == IntPtr.Zero; attempt++)
            {
                foreach (Process process in Process.GetProcesses())
                {
                    try
                    {
                        if (process.Id == Process.GetCurrentProcess().Id || process.MainWindowTitle != title) continue;
                        string otherVersion = FileVersionInfo.GetVersionInfo(process.MainModule.FileName).FileVersion;
                        if (string.Equals(otherVersion, version, StringComparison.OrdinalIgnoreCase))
                        {
                            window = process.MainWindowHandle;
                            break;
                        }
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
                if (window == IntPtr.Zero) Thread.Sleep(50);
            }
            if (window == IntPtr.Zero) return;
            if (IsIconic(window)) ShowWindowAsync(window, SW_RESTORE);
            SetForegroundWindow(window);
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string psz1, string psz2);
        public static int NaturalCompare(string a, string b)
        {
            try { return StrCmpLogicalW(a ?? "", b ?? ""); }
            catch { return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase); }
        }
    }
}
