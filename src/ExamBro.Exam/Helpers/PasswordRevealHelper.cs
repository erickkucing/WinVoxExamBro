using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace ExamBro.WpfHelpers;

/// <summary>
/// Coordinates bidirectional synchronization, caret placement, focus restoration,
/// and accessible state updates between a <see cref="PasswordBox"/>, its paired reveal
/// <see cref="TextBox"/>, and a show/hide <see cref="ToggleButton"/>.
/// </summary>
public sealed class PasswordRevealBinding
{
    private readonly PasswordBox _passwordBox;
    private readonly TextBox _revealTextBox;
    private readonly ToggleButton _toggleButton;
    private readonly Action? _onInputChanged;
    private readonly Action? _onEnterPressed;
    private bool _isSyncing;

    private PasswordRevealBinding(
        PasswordBox passwordBox,
        TextBox revealTextBox,
        ToggleButton toggleButton,
        Action? onInputChanged,
        Action? onEnterPressed)
    {
        _passwordBox = passwordBox;
        _revealTextBox = revealTextBox;
        _toggleButton = toggleButton;
        _onInputChanged = onInputChanged;
        _onEnterPressed = onEnterPressed;

        _passwordBox.PasswordChanged += OnPasswordChanged;
        _revealTextBox.TextChanged += OnRevealTextChanged;
        _passwordBox.KeyDown += OnKeyDown;
        _revealTextBox.KeyDown += OnKeyDown;
        _toggleButton.Checked += OnToggleChecked;
        _toggleButton.Unchecked += OnToggleUnchecked;

        UpdateToggleAccessibility(revealed: false);
    }

    public static PasswordRevealBinding Bind(
        PasswordBox passwordBox,
        TextBox revealTextBox,
        ToggleButton toggleButton,
        Action? onInputChanged = null,
        Action? onEnterPressed = null)
    {
        return new PasswordRevealBinding(passwordBox, revealTextBox, toggleButton, onInputChanged, onEnterPressed);
    }

    public string GetPassword() =>
        _toggleButton.IsChecked == true ? _revealTextBox.Text : _passwordBox.Password;

    public void Clear()
    {
        _isSyncing = true;
        try
        {
            _passwordBox.Password = string.Empty;
            _revealTextBox.Text = string.Empty;
        }
        finally
        {
            _isSyncing = false;
        }
    }

    public void Reset()
    {
        if (_toggleButton.IsChecked == true)
        {
            _toggleButton.IsChecked = false;
        }
        else
        {
            _revealTextBox.Visibility = Visibility.Collapsed;
            _passwordBox.Visibility = Visibility.Visible;
            UpdateToggleAccessibility(revealed: false);
        }

        Clear();
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            Reset();
        }

        _passwordBox.IsEnabled = enabled;
        _revealTextBox.IsEnabled = enabled;
        _toggleButton.IsEnabled = enabled;
    }

    public void SelectAllAndFocus()
    {
        if (_toggleButton.IsChecked == true)
        {
            _revealTextBox.SelectAll();
            FocusElement(_revealTextBox);
        }
        else
        {
            _passwordBox.SelectAll();
            FocusElement(_passwordBox);
        }
    }

    public void FocusActive()
    {
        if (_toggleButton.IsChecked == true)
        {
            _revealTextBox.CaretIndex = _revealTextBox.Text.Length;
            FocusElement(_revealTextBox);
        }
        else
        {
            FocusElement(_passwordBox);
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isSyncing) return;
        _onInputChanged?.Invoke();
    }

    private void OnRevealTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncing) return;

        _isSyncing = true;
        try
        {
            _passwordBox.Password = _revealTextBox.Text;
        }
        finally
        {
            _isSyncing = false;
        }

        _onInputChanged?.Invoke();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _onEnterPressed != null && _passwordBox.IsEnabled)
        {
            e.Handled = true;
            _onEnterPressed.Invoke();
        }
    }

    private void OnToggleChecked(object sender, RoutedEventArgs e)
    {
        _isSyncing = true;
        try
        {
            _revealTextBox.Text = _passwordBox.Password;
        }
        finally
        {
            _isSyncing = false;
        }

        _passwordBox.Visibility = Visibility.Collapsed;
        _revealTextBox.Visibility = Visibility.Visible;
        UpdateToggleAccessibility(revealed: true);

        _revealTextBox.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            _revealTextBox.CaretIndex = _revealTextBox.Text.Length;
            FocusElement(_revealTextBox);
        });
    }

    private void OnToggleUnchecked(object sender, RoutedEventArgs e)
    {
        _isSyncing = true;
        try
        {
            _passwordBox.Password = _revealTextBox.Text;
            _revealTextBox.Text = string.Empty;
        }
        finally
        {
            _isSyncing = false;
        }

        _revealTextBox.Visibility = Visibility.Collapsed;
        _passwordBox.Visibility = Visibility.Visible;
        UpdateToggleAccessibility(revealed: false);

        _passwordBox.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => FocusElement(_passwordBox));
    }

    private void UpdateToggleAccessibility(bool revealed)
    {
        string label = revealed ? "Sembunyikan kata sandi" : "Tampilkan kata sandi";
        _toggleButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(_toggleButton, label);
    }

    private static void FocusElement(IInputElement element)
    {
        if (element is FrameworkElement fe)
        {
            fe.Focus();
        }
        Keyboard.Focus(element);
    }
}
