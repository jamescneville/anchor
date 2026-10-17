using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Anchor.Core;

namespace Anchor.Desktop;

/// <summary>"Where was I?" (global hotkey) and the periodic focus check-in. Local text shows instantly; AI adds a next step.</summary>
public sealed class RecoveryWindow : Window
{
    readonly AssistantEngine _engine;
    readonly TextBlock _local = new() { FontSize = 14.5, LineHeight = 22, Margin = new Thickness(0, 0, 0, 14) };
    readonly TextBlock _ai = new() { FontSize = 14.5, LineHeight = 22, Foreground = Ui.B("Accent"), Margin = new Thickness(0, 0, 0, 14) };
    readonly TextBox _task = new() { Margin = new Thickness(0, 0, 0, 12) };
    readonly TextBox _crumb = new() { Margin = new Thickness(0, 0, 0, 16) };
    readonly bool _checkIn;

    public RecoveryWindow(AssistantEngine engine, bool checkIn)
    {
        _engine = engine;
        _checkIn = checkIn;
        Title = checkIn ? "Anchor · Focus check-in" : "Anchor · Where was I?";
        Topmost = true;
        Width = 540;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Ui.Style(this);

        var st = new StackPanel { Margin = new Thickness(26) };
        st.Children.Add(new TextBlock
        {
            Text = checkIn ? "Still on track?" : "Where was I?",
            FontSize = 26, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Application.Current.Resources["DisplayFont"], Margin = new Thickness(0, 0, 0, 14),
        });
        st.Children.Add(_local);
        st.Children.Add(_ai);
        st.Children.Add(Field("Right now: the one thing I'm doing", _task));
        st.Children.Add(Field("Breadcrumb: what's on my mind / where to pick back up (optional)", _crumb));

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var go = Ui.Primary(checkIn ? "Yes, continuing" : "Set & go");
        go.Click += (_, _) => Commit();
        var done = Ui.Secondary("Done ✓");
        done.IsEnabled = engine.Store.State.CurrentTask.Length > 0;
        done.ToolTip = "Finish the current task and clear it";
        done.Click += (_, _) => { _engine.SaveCheckpoint(_crumb.Text); _engine.FinishTask(); Close(); };
        var close = Ui.Secondary("Close");
        close.Click += (_, _) => Close();
        row.Children.Add(go); row.Children.Add(done); row.Children.Add(close);
        st.Children.Add(row);
        Content = st;

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); else if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift)) { Commit(); e.Handled = true; } };
        Loaded += async (_, _) =>
        {
            _task.Text = _engine.Store.State.CurrentTask;
            _crumb.Text = _engine.Store.State.Checkpoint;
            _task.Focus(); _task.SelectAll();
            Refresh();
            try
            {
                _ai.Text = "Thinking…";
                _ai.Text = await _engine.RecoverAiAsync() ?? "";
            }
            catch (Exception ex) { _ai.Text = "(AI unavailable: " + ex.Message + ")"; }
            _ai.Visibility = _ai.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        };
        Closed += (_, _) => { if (_checkIn) _engine.CheckInAnswered(); };
    }

    static UIElement Field(string label, TextBox box) => new StackPanel
    {
        Children = { new TextBlock { Text = label, FontSize = 12, Foreground = Ui.B("Dim"), Margin = new Thickness(0, 0, 0, 4) }, box },
    };

    void Refresh() => _local.Text = _engine.RecoverLocal();

    void Commit()
    {
        var t = _task.Text.Trim();
        var current = _engine.Store.State.CurrentTask;
        if (t.Length > 0 && t != current) _engine.SetCurrentTask(t);
        else if (t.Length > 0) _engine.KeepTask();                  // same task, confirmed: no longer stale
        _engine.SaveCheckpoint(_crumb.Text);                          // empty clears the breadcrumb
        Close();
    }
}
