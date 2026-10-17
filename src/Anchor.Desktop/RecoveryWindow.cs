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
    readonly TextBlock _ai = new() { FontSize = 14.5, LineHeight = 22, Foreground = (Brush)Application.Current.Resources["Accent"], Margin = new Thickness(0, 0, 0, 14) };
    readonly TextBox _task = new() { Margin = new Thickness(0, 0, 0, 8) };
    readonly bool _checkIn;

    public RecoveryWindow(AssistantEngine engine, bool checkIn)
    {
        _engine = engine;
        _checkIn = checkIn;
        Title = checkIn ? "Anchor · Focus check-in" : "Anchor · Where was I?";
        Topmost = true;
        Width = 520;
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
        st.Children.Add(new TextBlock { Text = "What I'm doing right now:", Foreground = (Brush)Application.Current.Resources["Dim"], Margin = new Thickness(0, 0, 0, 4) });
        st.Children.Add(_task);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var save = Ui.Primary(checkIn ? "Yes, continuing" : "Set & go");
        save.Click += (_, _) => Commit();
        var note = Ui.Secondary("Save as note");
        note.Click += (_, _) => { if (_task.Text.Trim().Length > 0) _engine.SaveCheckpoint(_task.Text.Trim()); Refresh(); };
        var askClaude = Ui.Secondary("Ask Claude");
        askClaude.Click += (_, _) => { try { _engine.AskClaude(ClaudeAsk.WhereWasI); } catch (Exception ex) { _ai.Text = "Couldn't open Claude desktop: " + ex.Message; _ai.Visibility = Visibility.Visible; } };
        var close = Ui.Secondary("Close");
        close.Click += (_, _) => Close();
        row.Children.Add(save); row.Children.Add(note); row.Children.Add(askClaude); row.Children.Add(close);
        st.Children.Add(row);
        Content = st;

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); if (e.Key == Key.Enter) Commit(); };
        Loaded += async (_, _) =>
        {
            _task.Text = _engine.Store.State.CurrentTask;
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

    void Refresh() => _local.Text = _engine.RecoverLocal();

    void Commit()
    {
        var t = _task.Text.Trim();
        if (t.Length > 0) _engine.SetCurrentTask(t);
        Close();
    }
}
