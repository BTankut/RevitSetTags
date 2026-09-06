using System;
using System.Globalization;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitSetTags.Handlers;
using TextBox = System.Windows.Forms.TextBox;
using Form = System.Windows.Forms.Form;

namespace RevitSetTags.UI
{
    /// <summary>
    /// Modeless window shown by the "Set Tags" ribbon button. Mirrors the dialog
    /// of the demoed plugin: pick elements, enter the start position and the
    /// shift between tags, then set or reset the tags.
    /// </summary>
    public class SetTagsWindow : Form
    {
        private readonly SetTagsHandler _handler;
        private readonly ExternalEvent _event;
        private bool _reshowAfterPick;

        private Button _pickButton;
        private Label _countLabel;
        private TextBox _startX, _startY, _startZ;
        private TextBox _shiftX, _shiftY, _shiftZ;
        private Button _setButton, _resetButton, _closeButton;
        private Label _statusLabel;

        private static SetTagsWindow _instance;

        public SetTagsWindow(UIApplication uiapp)
        {
            _handler = new SetTagsHandler { Bridge = new WindowBridge(this) };
            _event = ExternalEvent.Create(_handler);

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

        protected override bool ShowWithoutActivation => false;

        private void BuildUi()
        {
            Text = "Set Tags";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ClientSize = new System.Drawing.Size(360, 300);

            var selectionGroup = new GroupBox
            {
                Text = "1. Selection",
                Left = 12,
                Top = 10,
                Width = 336,
                Height = 62,
            };

            _pickButton = new Button
            {
                Text = "Pick elements",
                Left = 12,
                Top = 24,
                Width = 120,
                Height = 28,
            };
            _pickButton.Click += OnPickClicked;

            _countLabel = new Label
            {
                Text = "Tags selected: 0",
                Left = 144,
                Top = 30,
                Width = 180,
                Height = 20,
            };

            selectionGroup.Controls.Add(_pickButton);
            selectionGroup.Controls.Add(_countLabel);

            var orderingGroup = new GroupBox
            {
                Text = "2. Ordering (meters)",
                Left = 12,
                Top = 78,
                Width = 336,
                Height = 118,
            };

            _startX = MakeBox("1.0");
            _startY = MakeBox("1.0");
            _startZ = MakeBox("0.0");
            _shiftX = MakeBox("0.0");
            _shiftY = MakeBox("0.0");
            _shiftZ = MakeBox("0.1");

            AddRow(orderingGroup, "Start", _startX, _startY, _startZ, 26);
            AddRow(orderingGroup, "Shift", _shiftX, _shiftY, _shiftZ, 60);

            orderingGroup.Controls.Add(new Label
            {
                Text = "First tag head at Start; every next tag one Shift further.",
                Left = 14,
                Top = 92,
                Width = 310,
                Height = 18,
            });

            _setButton = new Button
            {
                Text = "Set tags",
                Left = 12,
                Top = 206,
                Width = 104,
                Height = 30,
            };
            _setButton.Click += OnSetClicked;

            _resetButton = new Button
            {
                Text = "Reset",
                Left = 128,
                Top = 206,
                Width = 104,
                Height = 30,
            };
            _resetButton.Click += OnResetClicked;

            _closeButton = new Button
            {
                Text = "Close",
                Left = 244,
                Top = 206,
                Width = 104,
                Height = 30,
            };
            _closeButton.Click += (s, e) => Hide();

            _statusLabel = new Label
            {
                Text = "Ready.",
                Left = 12,
                Top = 246,
                Width = 336,
                Height = 42,
            };

            Controls.Add(selectionGroup);
            Controls.Add(orderingGroup);
            Controls.Add(_setButton);
            Controls.Add(_resetButton);
            Controls.Add(_closeButton);
            Controls.Add(_statusLabel);

            AcceptButton = _setButton;
        }

        private static TextBox MakeBox(string defaultValue)
        {
            return new TextBox
            {
                Text = defaultValue,
                Width = 78,
            };
        }

        private static void AddRow(GroupBox group, string title, TextBox x, TextBox y, TextBox z, int top)
        {
            group.Controls.Add(new Label
            {
                Text = title,
                Left = 14,
                Top = top + 3,
                Width = 40,
            });

            int[] lefts = { 60, 154, 248 };
            TextBox[] boxes = { x, y, z };
            string[] axes = { "X", "Y", "Z" };

            for (int i = 0; i < 3; i++)
            {
                group.Controls.Add(new Label
                {
                    Text = axes[i],
                    Left = lefts[i],
                    Top = top + 3,
                    Width = 16,
                });
                boxes[i].Left = lefts[i] + 18;
                boxes[i].Top = top;
                group.Controls.Add(boxes[i]);
            }
        }

        private void OnPickClicked(object sender, EventArgs e)
        {
            _reshowAfterPick = true;
            _countLabel.Text = "Picking...";
            Hide();
            _handler.Mode = HandlerMode.PickElements;
            _event.Raise();
        }

        private void OnSetClicked(object sender, EventArgs e)
        {
            if (!TryParseVector(_startX, _startY, _startZ, out double sx, out double sy, out double sz) ||
                !TryParseVector(_shiftX, _shiftY, _shiftZ, out double dx, out double dy, out double dz))
            {
                _statusLabel.Text = "Invalid numbers. Use invariant decimals, e.g. 0.1";
                return;
            }

            _handler.Start = new XYZ(sx, sy, sz);
            _handler.Shift = new XYZ(dx, dy, dz);
            _handler.Mode = HandlerMode.SetTags;
            _event.Raise();
        }

        private void OnResetClicked(object sender, EventArgs e)
        {
            _handler.Mode = HandlerMode.ResetTags;
            _event.Raise();
        }

        private static bool TryParseVector(TextBox x, TextBox y, TextBox z, out double vx, out double vy, out double vz)
        {
            bool okX = double.TryParse((x.Text ?? "").Trim().Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out vx);
            bool okY = double.TryParse((y.Text ?? "").Trim().Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out vy);
            bool okZ = double.TryParse((z.Text ?? "").Trim().Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out vz);
            return okX && okY && okZ;
        }

        internal void OnApiStatus(string status)
        {
            if (_reshowAfterPick)
            {
                _reshowAfterPick = false;
                Show();
                Activate();
            }

            _statusLabel.Text = status;
            if (status != null && status.StartsWith("Tags selected:"))
            {
                _countLabel.Text = status;
            }
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
                if (_window.IsDisposed || _window.Disposing)
                {
                    return;
                }

                try
                {
                    _window.BeginInvoke(new Action(() => _window.OnApiStatus(status)));
                }
                catch (InvalidOperationException)
                {
                    // Window handle gone; nothing to update.
                }
            }
        }
    }
}
