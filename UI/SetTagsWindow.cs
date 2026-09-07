using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitSetTags.Handlers;
using TextBox = System.Windows.Forms.TextBox;
using Form = System.Windows.Forms.Form;
using Timer = System.Windows.Forms.Timer;
using Color = System.Drawing.Color;
using ComboBox = System.Windows.Forms.ComboBox;

namespace RevitSetTags.UI
{
    /// <summary>
    /// Modeless palette shown by the "Set Tags" ribbon button. Workflow:
    /// Get tags (select) → optional filter built from that selection →
    /// Pick direction (origin + optional direction point) → done, filter resets.
    /// "Spacing x" / "Shift x" (typing or -/+) re-lay out the tags selected in
    /// the view, or the last placed group when nothing is selected.
    /// </summary>
    public class SetTagsWindow : Form
    {
        private const int LiveUpdateDelayMs = 300;
        private const double Step = 0.1;
        private const string AllTagsText = "All selected tags";

        private readonly SetTagsHandler _handler;
        private readonly ExternalEvent _event;
        private readonly Timer _liveTimer;

        private Button _getTagsButton;
        private Button _pickDirectionButton;
        private ComboBox _filterBox;
        private Label _countLabel;
        private TextBox _spacingBox;
        private TextBox _shiftBox;
        private Label _statusLabel;

        private int _selectedCount;

        private static SetTagsWindow _instance;

        public SetTagsWindow(UIApplication uiapp)
        {
            _handler = new SetTagsHandler { Bridge = new WindowBridge(this) };
            _event = ExternalEvent.Create(_handler);
            _liveTimer = new Timer { Interval = LiveUpdateDelayMs };
            _liveTimer.Tick += OnLiveTimerTick;

            BuildUi();
        }

        public static void ShowOrActivate(UIApplication uiapp)
        {
            if (_instance == null || _instance.IsDisposed)
            {
                _instance = new SetTagsWindow(uiapp);
            }

            _instance.Show();
            _instance.Activate();
        }

        private void BuildUi()
        {
            Text = "ProEngineering Tools";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ClientSize = new Size(184, 226);

            _getTagsButton = new Button
            {
                Text = "Get tags",
                Left = 6,
                Top = 6,
                Width = 172,
                Height = 24,
            };
            _getTagsButton.Click += OnGetTagsClicked;

            _filterBox = new ComboBox
            {
                Left = 6,
                Top = 34,
                Width = 172,
                DropDownStyle = ComboBoxStyle.DropDownList,
                DropDownWidth = 260,
            };
            ResetFilter(new List<TagTypeFilter>());
            _filterBox.SelectedIndexChanged += OnFilterChanged;

            _pickDirectionButton = new Button
            {
                Text = "Pick direction",
                Left = 6,
                Top = 60,
                Width = 172,
                Height = 24,
            };
            _pickDirectionButton.Click += (s, e) => StartPlaceColumn((ModifierKeys & Keys.Control) == Keys.Control);
            _pickDirectionButton.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) StartPlaceColumn(true); };

            _countLabel = new Label
            {
                Text = "Tags count: 0",
                Left = 8,
                Top = 92,
                Width = 170,
                Height = 18,
            };

            var spacingLabel = new Label
            {
                Text = "Spacing x:",
                Left = 8,
                Top = 124,
                Width = 68,
                Height = 18,
            };

            _spacingBox = new TextBox
            {
                Text = "0.6",
                Left = 78,
                Top = 120,
                Width = 52,
            };
            _spacingBox.TextChanged += OnValueChanged;
            _spacingBox.KeyDown += OnValueKeyDown;

            Button spacingMinus = MakeStepButton("-", 134, 120);
            spacingMinus.Click += (s, e) => StepValue(_spacingBox, -Step, false);
            Button spacingPlus = MakeStepButton("+", 156, 120);
            spacingPlus.Click += (s, e) => StepValue(_spacingBox, +Step, false);

            var shiftLabel = new Label
            {
                Text = "Shift x:",
                Left = 8,
                Top = 154,
                Width = 68,
                Height = 18,
            };

            _shiftBox = new TextBox
            {
                Text = "0.30",
                Left = 78,
                Top = 150,
                Width = 52,
            };
            _shiftBox.TextChanged += OnValueChanged;
            _shiftBox.KeyDown += OnValueKeyDown;

            Button shiftMinus = MakeStepButton("-", 134, 150);
            shiftMinus.Click += (s, e) => StepValue(_shiftBox, -Step, true);
            Button shiftPlus = MakeStepButton("+", 156, 150);
            shiftPlus.Click += (s, e) => StepValue(_shiftBox, +Step, true);

            _statusLabel = new Label
            {
                Text = "Ready.",
                Left = 8,
                Top = 186,
                Width = 170,
                Height = 34,
                AutoEllipsis = true,
                ForeColor = Color.DimGray,
            };

            var tips = new ToolTip();
            tips.SetToolTip(_getTagsButton, "1. Select the tags with Revit's selection first (window selection), then click here: no Finish needed. With nothing selected, a pick mode with Finish starts.");
            tips.SetToolTip(_filterBox, "2. Optional: keep only one tag category / family of the selection.");
            tips.SetToolTip(_pickDirectionButton, "3. Click the column origin; the column goes straight down. Right-click or Ctrl+click here to also pick a direction point. The tags are placed and the filter resets.");
            tips.SetToolTip(_spacingBox, "Distance between tag rows, in meters (0.6 m matches the original tool: 2 ft). Applies live to the tags selected in the view, else to the last placed group.");
            tips.SetToolTip(_shiftBox, "Leader shoulder length from the text edge to the elbow, in meters (0.30 m matches the original tool: 1 ft). Applies live to the tags selected in the view, else to the last placed group.");

            Controls.Add(_getTagsButton);
            Controls.Add(_filterBox);
            Controls.Add(_pickDirectionButton);
            Controls.Add(_countLabel);
            Controls.Add(spacingLabel);
            Controls.Add(_spacingBox);
            Controls.Add(spacingMinus);
            Controls.Add(spacingPlus);
            Controls.Add(shiftLabel);
            Controls.Add(_shiftBox);
            Controls.Add(shiftMinus);
            Controls.Add(shiftPlus);
            Controls.Add(_statusLabel);
        }

        private static Button MakeStepButton(string text, int left, int top)
        {
            return new Button
            {
                Text = text,
                Left = left,
                Top = top,
                Width = 20,
                Height = 22,
                TabStop = false,
            };
        }

        /// <summary>Rebuilds the filter list from a selection and resets it to "all".</summary>
        private void ResetFilter(List<TagTypeFilter> types)
        {
            _filterBox.BeginUpdate();
            _filterBox.Items.Clear();
            _filterBox.Items.Add(AllTagsText);
            foreach (TagTypeFilter type in types)
            {
                _filterBox.Items.Add(type);
            }

            _filterBox.SelectedIndex = 0;
            _filterBox.EndUpdate();
            _handler.Filter = null;
        }

        /// <summary>Handler callback after Get tags: filter entries of the selection.</summary>
        internal void OnSelection(List<TagTypeFilter> types, int count)
        {
            _selectedCount = count;
            ResetFilter(types);
            _countLabel.Text = "Tags count: " + count;
        }

        /// <summary>Handler callback after a column was placed: the command is over.</summary>
        internal void OnPlaced()
        {
            _selectedCount = 0;
            ResetFilter(new List<TagTypeFilter>());
        }

        private void OnFilterChanged(object sender, EventArgs e)
        {
            TagTypeFilter filter = _filterBox.SelectedItem as TagTypeFilter;
            _handler.Filter = filter;
            if (_selectedCount > 0)
            {
                _countLabel.Text = "Tags count: " + (filter == null ? _selectedCount : filter.Count);
            }

            if (filter != null)
            {
                _statusLabel.Text = "Filter: " + filter.Display + ". Now Pick direction.";
            }
        }

        private static bool TryReadNumber(TextBox box, out double value)
        {
            return double.TryParse((box.Text ?? "").Trim().Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);
        }

        private void StepValue(TextBox box, double delta, bool isShift)
        {
            if (!TryReadNumber(box, out double value))
            {
                value = 1.0;
            }

            value = Math.Round(value + delta, 2);
            if (isShift)
            {
                value = Math.Max(0, value);
                box.Text = value.ToString("0.00", CultureInfo.InvariantCulture);
            }
            else
            {
                value = Math.Max(Step, value);
                box.Text = value.ToString("0.##", CultureInfo.InvariantCulture);
            }
        }

        private bool TryReadValues(out double spacing, out double shift)
        {
            shift = 0;
            if (!TryReadNumber(_spacingBox, out spacing) || spacing <= 0)
            {
                _statusLabel.Text = "Invalid Spacing x. Use a positive number, e.g. 0.6";
                return false;
            }

            if (!TryReadNumber(_shiftBox, out shift))
            {
                _statusLabel.Text = "Invalid Shift x. Use a number, e.g. 0.30";
                return false;
            }

            shift = Math.Abs(shift);
            return true;
        }

        private void OnGetTagsClicked(object sender, EventArgs e)
        {
            _liveTimer.Stop();
            _countLabel.Text = "Picking...";
            _statusLabel.Text = "Reading the selection (or pick tags, then Finish).";
            _handler.Mode = HandlerMode.GetTags;
            _event.Raise();
        }

        private void StartPlaceColumn(bool withDirectionPoint)
        {
            _liveTimer.Stop();
            if (!TryReadValues(out double spacing, out double shift))
            {
                return;
            }

            _handler.SpacingMeters = spacing;
            _handler.ShiftMeters = shift;
            _handler.Filter = _filterBox.SelectedItem as TagTypeFilter;
            _handler.PickDirectionPoint = withDirectionPoint;
            _statusLabel.Text = withDirectionPoint
                ? "Click the column origin, then a direction point (Esc = straight down)."
                : "Click the column origin (column goes straight down).";
            _handler.Mode = HandlerMode.PlaceColumn;
            _event.Raise();
        }

        private void OnValueChanged(object sender, EventArgs e)
        {
            _liveTimer.Stop();
            _liveTimer.Start();
        }

        private void OnValueKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                _liveTimer.Stop();
                ApplyLiveValues();
            }
        }

        private void OnLiveTimerTick(object sender, EventArgs e)
        {
            _liveTimer.Stop();
            ApplyLiveValues();
        }

        private void ApplyLiveValues()
        {
            if (!TryReadValues(out double spacing, out double shift))
            {
                return;
            }

            _handler.SpacingMeters = spacing;
            _handler.ShiftMeters = shift;
            _handler.Mode = HandlerMode.Relayout;
            _event.Raise();
        }

        internal void OnApiStatus(string status)
        {
            if (status != null && status.StartsWith("Tags count:"))
            {
                int dot = status.IndexOf('.');
                _countLabel.Text = dot > 0 ? status.Substring(0, dot) : status;
                _statusLabel.Text = dot > 0 ? status.Substring(dot + 1).Trim() : "";
                return;
            }

            _statusLabel.Text = status;
            if (_countLabel.Text == "Picking...")
            {
                _countLabel.Text = "Tags count: 0";
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _liveTimer.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Marshals handler results back to the UI thread; the handler runs on
        /// the Revit API thread.
        /// </summary>
        public class WindowBridge
        {
            private readonly SetTagsWindow _window;

            public WindowBridge(SetTagsWindow window)
            {
                _window = window;
            }

            public void ReportStatus(string status)
            {
                Post(() => _window.OnApiStatus(status));
            }

            public void ReportSelection(List<TagTypeFilter> types, int count)
            {
                Post(() => _window.OnSelection(types, count));
            }

            public void ReportPlaced()
            {
                Post(() => _window.OnPlaced());
            }

            private void Post(Action action)
            {
                if (_window.IsDisposed || _window.Disposing)
                {
                    return;
                }

                try
                {
                    _window.BeginInvoke(action);
                }
                catch (InvalidOperationException)
                {
                    // Window handle gone; nothing to update.
                }
            }
        }
    }
}
