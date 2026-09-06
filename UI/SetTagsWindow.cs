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
    /// Modeless window shown by the "Set Tags" ribbon button. Recreates the
    /// "ProEngineering Bim" palette of the demoed plugin: Get tags, Tags count,
    /// Spacing (m), and Shift (m) with Up/Down post-correction buttons.
    /// </summary>
    public class SetTagsWindow : Form
    {
        private readonly SetTagsHandler _handler;
        private readonly ExternalEvent _event;

        private Button _getTagsButton;
        private Label _countLabel;
        private TextBox _spacingBox;
        private TextBox _shiftBox;
        private Button _upButton, _downButton;
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

        private void BuildUi()
        {
            Text = "ProEngineering Bim";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ClientSize = new System.Drawing.Size(320, 220);

            var selectionGroup = new GroupBox
            {
                Text = "Tags",
                Left = 12,
                Top = 10,
                Width = 296,
                Height = 64,
            };

            _getTagsButton = new Button
            {
                Text = "Get tags",
                Left = 12,
                Top = 26,
                Width = 110,
                Height = 28,
            };
            _getTagsButton.Click += OnGetTagsClicked;

            _countLabel = new Label
            {
                Text = "Tags count: 0",
                Left = 136,
                Top = 32,
                Width = 150,
                Height = 20,
            };

            selectionGroup.Controls.Add(_getTagsButton);
            selectionGroup.Controls.Add(_countLabel);

            var columnGroup = new GroupBox
            {
                Text = "Column",
                Left = 12,
                Top = 80,
                Width = 296,
                Height = 58,
            };

            columnGroup.Controls.Add(new Label
            {
                Text = "Spacing (m):",
                Left = 12,
                Top = 27,
                Width = 82,
            });

            _spacingBox = new TextBox
            {
                Text = "1",
                Left = 100,
                Top = 24,
                Width = 70,
            };
            columnGroup.Controls.Add(_spacingBox);

            var correctionGroup = new GroupBox
            {
                Text = "Post-correction",
                Left = 12,
                Top = 144,
                Width = 296,
                Height = 58,
            };

            correctionGroup.Controls.Add(new Label
            {
                Text = "Shift (m):",
                Left = 12,
                Top = 27,
                Width = 70,
            });

            _shiftBox = new TextBox
            {
                Text = "1.00",
                Left = 88,
                Top = 24,
                Width = 70,
            };
            correctionGroup.Controls.Add(_shiftBox);

            _upButton = new Button
            {
                Text = "▲",
                Left = 170,
                Top = 22,
                Width = 52,
                Height = 28,
            };
            _upButton.Click += (s, e) => OnNudgeClicked(HandlerMode.NudgeUp);

            _downButton = new Button
            {
                Text = "▼",
                Left = 228,
                Top = 22,
                Width = 52,
                Height = 28,
            };
            _downButton.Click += (s, e) => OnNudgeClicked(HandlerMode.NudgeDown);

            correctionGroup.Controls.Add(_upButton);
            correctionGroup.Controls.Add(_downButton);

            _statusLabel = new Label
            {
                Text = "Ready.",
                Left = 12,
                Top = 176,
                Width = 296,
                Height = 38,
            };

            Controls.Add(selectionGroup);
            Controls.Add(columnGroup);
            Controls.Add(correctionGroup);
            Controls.Add(_statusLabel);
        }

        private bool TryReadPositiveMeters(TextBox box, out double meters)
        {
            bool ok = double.TryParse((box.Text ?? "").Trim().Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out meters);
            return ok && meters > 0;
        }

        private void OnGetTagsClicked(object sender, EventArgs e)
        {
            if (!TryReadPositiveMeters(_spacingBox, out double spacing))
            {
                _statusLabel.Text = "Invalid spacing. Use a positive number, e.g. 1";
                return;
            }

            _handler.SpacingMeters = spacing;
            _countLabel.Text = "Picking...";
            _handler.Mode = HandlerMode.GetTags;
            _event.Raise();
        }

        private void OnNudgeClicked(HandlerMode mode)
        {
            if (!TryReadPositiveMeters(_shiftBox, out double shift))
            {
                _statusLabel.Text = "Invalid shift. Use a positive number, e.g. 0.5";
                return;
            }

            _handler.ShiftMeters = shift;
            _handler.Mode = mode;
            _event.Raise();
        }

        internal void OnApiStatus(string status)
        {
            _statusLabel.Text = status;
            if (status != null && status.StartsWith("Tags count:"))
            {
                int dot = status.IndexOf('.');
                _countLabel.Text = dot > 0 ? status.Substring(0, dot) : status;
            }
            else if (status == "Selection cancelled.")
            {
                _countLabel.Text = "Tags count: 0";
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
