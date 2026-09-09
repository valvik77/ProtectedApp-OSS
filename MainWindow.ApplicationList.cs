using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ProtectedApp.Models;
using ProtectedApp.Services;
using ProtectedApp.Shared;
using Windows.Storage.Streams;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private async void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProtectedApplication app) return;
        var name = new TextBox { Header = "Nombre", Text = app.Name };
        var category = CreateCategoryEditor(app.Category);
        var passwordMode = new ComboBox { Header = "Contraseña", HorizontalAlignment = HorizontalAlignment.Stretch };
        passwordMode.Items.Add(new ComboBoxItem { Content = "Mantener configuración actual", Tag = "keep" });
        passwordMode.Items.Add(new ComboBoxItem { Content = "Usar contraseña maestra", Tag = "master" });
        passwordMode.Items.Add(new ComboBoxItem { Content = "Definir contraseña propia", Tag = "own" });
        passwordMode.SelectedIndex = 0;
        var password = new PasswordBox { Header = "Nueva contraseña propia", PasswordRevealMode = PasswordRevealMode.Peek, Visibility = Visibility.Collapsed };
        var confirmation = new PasswordBox { Header = "Confirmar contraseña", PasswordRevealMode = PasswordRevealMode.Peek, Visibility = Visibility.Collapsed };
        passwordMode.SelectionChanged += (_, _) =>
        {
            var visible = (passwordMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "own" ? Visibility.Visible : Visibility.Collapsed;
            password.Visibility = visible;
            confirmation.Visibility = visible;
        };
        var timePolicy = CreateTimePolicyEditor(app.UnlockGraceMinutes, app.ForceCloseAfterMinutes, app.ForceCloseAfterInactivityMinutes);
        var schedule = CreateScheduleEditor(app.ScheduleEnabled, app.ScheduleDays, app.ScheduleStartMinutes, app.ScheduleEndMinutes, app.BlockOutsideSchedule);
        var error = new TextBlock { Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 255, 85, 85), Windows.UI.Color.FromArgb(255, 248, 81, 73)), FontSize = 12 };
        var panel = new StackPanel { Spacing = 10, Width = 420 };
        panel.Children.Add(name); panel.Children.Add(category); panel.Children.Add(passwordMode); panel.Children.Add(password); panel.Children.Add(confirmation); panel.Children.Add(timePolicy.Panel); panel.Children.Add(schedule.Panel); panel.Children.Add(error);
        var dialog = CreateDialog("Editar protección", CreateDialogScroller(panel), "Guardar", "Cancelar");
        var valid = false;
        dialog.PrimaryButtonClick += (dialogSender, args) =>
        {
            var mode = (passwordMode.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (string.IsNullOrWhiteSpace(name.Text)) { error.Text = LocalizationService.T("Indica un nombre."); args.Cancel = true; return; }
            if (mode == "own" && (password.Password.Length < PasswordService.MinimumPasswordLength || password.Password != confirmation.Password)) { error.Text = LocalizationService.T("La nueva contraseña debe coincidir y tener al menos 12 caracteres."); args.Cancel = true; return; }
            if (!TryReadTimePolicy(timePolicy, out _, out _, out _, out var timeError)) { error.Text = LocalizationService.T(timeError); args.Cancel = true; return; }
            if (!TryReadSchedule(schedule, out _, out _, out _, out _, out _, out var scheduleError)) { error.Text = LocalizationService.T(scheduleError); args.Cancel = true; return; }
            valid = true;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !valid) return;
        app.Name = name.Text.Trim();
        app.Category = string.IsNullOrWhiteSpace(category.Text) ? "General" : category.Text.Trim();
        TryReadTimePolicy(timePolicy, out var unlockMinutes, out var forceCloseMinutes, out var inactiveCloseMinutes, out _);
        app.UnlockGraceMinutes = unlockMinutes;
        app.ForceCloseAfterMinutes = forceCloseMinutes;
        app.ForceCloseAfterInactivityMinutes = inactiveCloseMinutes;
        TryReadSchedule(schedule, out var scheduleEnabled, out var scheduleDays, out var scheduleStart, out var scheduleEnd, out var blockOutsideSchedule, out _);
        app.ScheduleEnabled = scheduleEnabled; app.ScheduleDays = scheduleDays; app.ScheduleStartMinutes = scheduleStart; app.ScheduleEndMinutes = scheduleEnd; app.BlockOutsideSchedule = blockOutsideSchedule;
        var selectedMode = (passwordMode.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (selectedMode == "master") { app.PasswordHash = null; app.PasswordSalt = null; }
        if (selectedMode == "own") { var hashed = PasswordService.Hash(password.Password); app.PasswordHash = hashed.Hash; app.PasswordSalt = hashed.Salt; }
        await SaveAsync();
        RefreshCategoryChips();
        RefreshVisibleApps();
        AddActivity(app.Name, "Regla actualizada");
    }

    private async void LockRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProtectedApplication app) return;
        if (!_guardianManaged) { await ShowMessageAsync("Guardian no disponible", "No se puede bloquear una aplicación mientras el servicio Guardian no esté activo. Repara el servicio desde Configuración."); return; }
        var lockTitle = LocalizationService.IsEnglish ? $"Lock {app.Name}" : $"Bloquear {app.Name}";
        var lockDescription = LocalizationService.IsEnglish
            ? $"Only {app.Name} access will be revoked and its open processes will be closed. Unsaved work may be lost."
            : $"Se revocará únicamente el acceso de {app.Name} y se cerrarán sus procesos abiertos. El trabajo no guardado podría perderse.";
        var dialog = CreateDialog(lockTitle, new TextBlock { Text = lockDescription, TextWrapping = TextWrapping.Wrap }, "Bloquear", "Cancelar");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (_guardianToken is null && !await VerifyMasterAsync($"Autorizar bloqueo de {app.Name}")) return;
        var response = await _guardianClient.LockRuleAsync(app.Id, _guardianToken ?? string.Empty);
        if (!response.Success && response.Error?.Contains("Autorización de administración no válida", StringComparison.OrdinalIgnoreCase) == true) { _guardianToken = null; if (!await VerifyMasterAsync($"Autorizar bloqueo de {app.Name}")) return; response = await _guardianClient.LockRuleAsync(app.Id, _guardianToken ?? string.Empty); }
        if (!response.Success) { await ShowMessageAsync("No se pudo bloquear", LocalizationService.UserFacingMessage(response.Error, "Guardian no confirmó el bloqueo selectivo.")); return; }
        _monitor.CancelAuthorizedLaunch(app); _monitor.Resolve(app);
        AddActivity(app.Name, response.AffectedProcessCount == 1 ? "Bloqueo selectivo aplicado; 1 proceso finalizado" : $"Bloqueo selectivo aplicado; {response.AffectedProcessCount} procesos finalizados");
        await SaveAsync();
    }

    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProtectedApplication app) return;
        var removalDescription = LocalizationService.IsEnglish
            ? $"{app.Name} can be opened again without a password."
            : $"{app.Name} podrá volver a abrirse sin contraseña.";
        var dialog = CreateDialog("Eliminar protección", new TextBlock { Text = removalDescription, TextWrapping = TextWrapping.Wrap }, "Eliminar", "Cancelar");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        app.PropertyChanged -= Rule_PropertyChanged;
        Applications.Remove(app);
        _monitor.Resolve(app);
        await SaveAsync();
        RefreshVisibleApps();
        AddActivity(app.Name, "Protección eliminada");
    }

    private async void RuleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not ToggleSwitch toggle || !toggle.IsEnabled
            || toggle.DataContext is not ProtectedApplication app) return;

        var requestedState = toggle.IsOn;
        var currentState = app.IsEnabled;
        if (requestedState == currentState) return;

        // The control must not advertise a changed state until Guardian has
        // accepted the policy. Otherwise a launch in that small interval can
        // be evaluated using the prior rule and show a misleading password UI.
        toggle.IsEnabled = false;
        toggle.IsOn = currentState;
        _pendingRuleProtectionStates[app.Id] = requestedState;
        try
        {
            if (!_guardianManaged || string.IsNullOrWhiteSpace(_guardianToken))
            {
                await ShowMessageAsync("Guardian no disponible",
                    "No se pudo aplicar el cambio de protección. Repara o desbloquea Guardian e inténtalo de nuevo.");
                return;
            }

            _state.Applications = Applications.ToList();
            var response = await _guardianClient.SetApplicationEnabledAsync(
                _state, app.Id, requestedState, _guardianToken);
            if (!response.Success)
            {
                await ShowMessageAsync("No se pudo cambiar la protección",
                    response.Error ?? "Guardian no confirmó el cambio de la regla.");
                return;
            }

            app.IsEnabled = requestedState;
            await SaveAsync(synchronizeGuardian: false);
            RefreshStats();
            AddActivity(app.Name, requestedState ? "Protección activada" : "Protección desactivada");
            if (!requestedState && _suppressedRuleLaunches.Remove(app.Id))
                RestartLaunchAfterProtectionDisabled(app);
        }
        finally
        {
            _pendingRuleProtectionStates.Remove(app.Id);
            toggle.IsEnabled = true;
        }
    }

    private static void RestartLaunchAfterProtectionDisabled(ProtectedApplication app)
    {
        try
        {
            var launch = ProtectedTarget.GetLaunchCommand(app.Path);
            Process.Start(new ProcessStartInfo(launch.Executable, launch.Arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(app.Path)
            });
        }
        catch
        {
            // The user can start the app normally if Windows rejects the
            // original launch command after the protection transition.
        }
    }

    private void Rule_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProtectedApplication.IsEnabled)) DispatcherQueue.TryEnqueue(RefreshStats);
    }

    private async Task ConfigureNewApplicationAsync(string suggestedName, string executablePath)
    {
        if (!File.Exists(executablePath) || !ProtectedTarget.IsSupported(executablePath)) { await ShowMessageAsync("Archivo no compatible", "Selecciona un ejecutable .exe, un script por lotes .bat o un script de Python .py."); return; }
        if ((!string.IsNullOrWhiteSpace(Environment.ProcessPath) && PathsEqual(executablePath, Environment.ProcessPath)) || Path.GetFileName(executablePath).Equals("ProtectedApp.Guardian.exe", StringComparison.OrdinalIgnoreCase) || IsCriticalWindowsExecutable(executablePath)) { await ShowMessageAsync("Aplicación no permitida", "ProtectedApp no permite proteger sus propios componentes ni procesos esenciales de Windows porque podría dejar la sesión inutilizable."); return; }
        var name = new TextBox { Header = "Nombre", Text = suggestedName }; var category = CreateCategoryEditor("General");
        var password = new PasswordBox { Header = "Contraseña propia (opcional)", PlaceholderText = "Vacío = usar contraseña maestra", PasswordRevealMode = PasswordRevealMode.Peek }; var confirmation = new PasswordBox { Header = "Confirmar contraseña", PasswordRevealMode = PasswordRevealMode.Peek };
        var timePolicy = CreateTimePolicyEditor(0, 0, 0); var schedule = CreateScheduleEditor(false, (int)ScheduleDays.EveryDay, 9 * 60, 17 * 60, false);
        var error = new TextBlock { Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255,255,85,85), Windows.UI.Color.FromArgb(255,248,81,73)), FontSize = 12 }; var panel = new StackPanel { Spacing = 10, Width = 420 };
        panel.Children.Add(new TextBlock { Text = executablePath, Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255,98,114,164), Windows.UI.Color.FromArgb(255,118,118,118)), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(name); panel.Children.Add(category); panel.Children.Add(password); panel.Children.Add(confirmation); panel.Children.Add(timePolicy.Panel); panel.Children.Add(schedule.Panel); panel.Children.Add(error);
        var dialog = CreateDialog("Añadir aplicación", CreateDialogScroller(panel), "Añadir", "Cancelar"); var valid = false;
        dialog.PrimaryButtonClick += (dialogSender, args) => { if (string.IsNullOrWhiteSpace(name.Text)) { error.Text=LocalizationService.T("Indica un nombre."); args.Cancel=true; return; } if (password.Password != confirmation.Password) { error.Text=LocalizationService.T("Las contraseñas no coinciden."); args.Cancel=true; return; } if (password.Password.Length is > 0 and < PasswordService.MinimumPasswordLength) { error.Text=LocalizationService.T("La contraseña propia debe tener al menos 12 caracteres."); args.Cancel=true; return; } if (!TryReadTimePolicy(timePolicy,out _,out _,out _,out var timeError)) { error.Text=LocalizationService.T(timeError); args.Cancel=true; return; } if (!TryReadSchedule(schedule,out _,out _,out _,out _,out _,out var scheduleError)) { error.Text=LocalizationService.T(scheduleError); args.Cancel=true; return; } valid=true; };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !valid) return;
        TryReadTimePolicy(timePolicy, out var unlockMinutes, out var forceCloseMinutes, out var inactiveCloseMinutes, out _); TryReadSchedule(schedule, out var scheduleEnabled, out var scheduleDays, out var scheduleStart, out var scheduleEnd, out var blockOutsideSchedule, out _);
        var app = new ProtectedApplication { Name=name.Text.Trim(), Path=executablePath, Category=string.IsNullOrWhiteSpace(category.Text)?"General":category.Text.Trim(), UnlockGraceMinutes=unlockMinutes, ForceCloseAfterMinutes=forceCloseMinutes, ForceCloseAfterInactivityMinutes=inactiveCloseMinutes, ScheduleEnabled=scheduleEnabled, ScheduleDays=scheduleDays, ScheduleStartMinutes=scheduleStart, ScheduleEndMinutes=scheduleEnd, BlockOutsideSchedule=blockOutsideSchedule };
        if (!string.IsNullOrEmpty(password.Password)) { var hashed=PasswordService.Hash(password.Password); app.PasswordHash=hashed.Hash; app.PasswordSalt=hashed.Salt; }
        app.PropertyChanged += Rule_PropertyChanged; await PrepareProtectedApplicationIconsAsync([app]); Applications.Add(app); await SaveAsync(); RefreshCategoryChips(); RefreshVisibleApps(); AddActivity(app.Name, "Protección añadida");
    }

    private async void AddApplicationButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = await SelectApplicationAsync();
        if (selected is null) return;
        if (Applications.Any(a => PathsEqual(a.Path, selected.Path))) { await ShowMessageAsync("Ya está protegido", "Ese archivo ya aparece en tu lista."); return; }
        if (PathsEqual(selected.Path, Environment.ProcessPath ?? string.Empty)) { await ShowMessageAsync("Acción no disponible", "ProtectedApp no puede proteger su propio proceso."); return; }
        await ConfigureNewApplicationAsync(selected.Name, selected.Path);
    }

    private async Task<InstalledApplication?> SelectApplicationAsync()
    {
        var installed = await InstalledAppsService.GetInstalledApplicationsAsync();
        await PrepareInstalledApplicationIconsAsync(installed);
        var visible = new ObservableCollection<InstalledApplication>(installed);
        var search = new TextBox { PlaceholderText = "Buscar aplicaciones instaladas", Padding = new Thickness(12, 8, 12, 8) };
        var list = new ListView { ItemsSource = visible, SelectionMode = ListViewSelectionMode.Single, ItemTemplate = (DataTemplate)Root.Resources["InstalledAppTemplate"], HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var empty = new TextBlock { Text = "No se encontraron aplicaciones", Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 98, 114, 164), Windows.UI.Color.FromArgb(255, 118, 118, 118)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, IsHitTestVisible = false };
        var listHost = new Grid(); listHost.Children.Add(list); listHost.Children.Add(empty);
        listHost.SizeChanged += (_, _) => empty.Width = listHost.ActualWidth;
        empty.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var layout = new Grid { Width = 620, Height = 430, RowSpacing = 10 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(search); Grid.SetRow(listHost, 1); layout.Children.Add(listHost);
        var note = new TextBlock { Text = $"{installed.Count} aplicaciones de escritorio detectadas", Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 98, 114, 164), Windows.UI.Color.FromArgb(255, 118, 118, 118)), FontSize = 10 };
        Grid.SetRow(note, 2); layout.Children.Add(note);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Style = Application.Current.Resources["ProtectedContentDialogStyle"] as Style, Title = "Añadir aplicación", Content = layout, PrimaryButtonText = "Proteger seleccionada", SecondaryButtonText = "Elegir archivo…", CloseButtonText = "Cancelar", IsPrimaryButtonEnabled = false, DefaultButton = ContentDialogButton.Primary };
        ApplyDialogPalette(dialog); dialog.Loaded += Dialog_Loaded; TrackDialogActivity(dialog);
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;
        var protectSelectionRequested = false;
        list.DoubleTapped += (_, _) =>
        {
            if (list.SelectedItem is null) return;
            protectSelectionRequested = true;
            dialog.Hide();
        };
        search.TextChanged += (_, _) =>
        {
            var query = search.Text.Trim(); visible.Clear();
            foreach (var item in installed.Where(item => query.Length == 0 || item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.Publisher.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.Path.Contains(query, StringComparison.OrdinalIgnoreCase))) visible.Add(item);
            empty.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary || protectSelectionRequested) return list.SelectedItem as InstalledApplication;
        if (result != ContentDialogResult.Secondary) return null;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Desktop };
        picker.FileTypeFilter.Add(".exe"); picker.FileTypeFilter.Add(".bat"); picker.FileTypeFilter.Add(".py");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        Windows.Storage.StorageFile? file;
        try { file = await picker.PickSingleFileAsync(); } finally { RestoreMainWindowFocus(); }
        return file is null ? null : new InstalledApplication { Name = Path.GetFileNameWithoutExtension(file.Name), Path = file.Path, Source = ProtectedTarget.IsScript(file.Path) ? "Script" : "Ejecutable" };
    }

    private static async Task PrepareInstalledApplicationIconsAsync(IEnumerable<InstalledApplication> applications)
    {
        foreach (var application in applications)
            application.IconSource = await CreateIconSourceAsync(application.IconPng);
    }

    private static async Task PrepareProtectedApplicationIconsAsync(IEnumerable<ProtectedApplication> applications)
    {
        var items = applications.ToArray();
        var iconData = await Task.Run(() => items.Select(application => ApplicationIconService.GetIconPng(application.Path)).ToArray());
        for (var index = 0; index < items.Length; index++)
            items[index].IconSource = await CreateIconSourceAsync(iconData[index]);
    }

    private static async Task<BitmapImage> CreateIconSourceAsync(byte[]? iconPng)
    {
        if (iconPng is null || iconPng.Length == 0) return new BitmapImage(new Uri("ms-appx:///Assets/BrandShield.png"));
        try
        {
            var bitmap = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(iconPng);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch { return new BitmapImage(new Uri("ms-appx:///Assets/BrandShield.png")); }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshVisibleApps();
    private void FilterButton_Click(object sender, RoutedEventArgs e) => RefreshVisibleApps();

    private void RefreshCategoryChips()
    {
        if (!_initialized) return;
        CategoryChipsPanel.Children.Clear();
        var categories = new List<string> { "all" };
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in Applications)
        {
            var category = string.IsNullOrWhiteSpace(app.Category) ? "General" : app.Category.Trim();
            if (known.Add(category)) categories.Add(category);
        }
        if (!known.Contains("General")) categories.Add("General");

        foreach (var category in categories)
        {
            var isAll = category == "all";
            var label = isAll ? LocalizationService.T("Todos") : category;
            var isSelected = string.Equals(_selectedCategory, category, StringComparison.OrdinalIgnoreCase);
            var chip = new Button
            {
                Content = label,
                Tag = category,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 4, 12, 4),
                FontSize = 11,
                FontWeight = isSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Background = isSelected ? (Brush)Application.Current.Resources["AccentIndigoBrush"] : (Brush)Application.Current.Resources["ControlSurfaceBrush"],
                Foreground = isSelected ? new SolidColorBrush(Microsoft.UI.Colors.White) : (Brush)Application.Current.Resources["PrimaryTextBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeBrush"],
                BorderThickness = new Thickness(1),
                Style = Application.Current.Resources["RoundedDialogButtonStyle"] as Style
            };
            chip.Click += (sender, _) =>
            {
                _selectedCategory = (string)((Button)sender).Tag;
                RefreshCategoryChips();
                RefreshVisibleApps();
            };
            CategoryChipsPanel.Children.Add(chip);
        }
    }

    private void RefreshVisibleApps()
    {
        if (!_initialized) return;
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var selected = Applications.Where(application =>
            (!EnabledOnlyButton.IsChecked.GetValueOrDefault() || application.IsEnabled)
            && (_selectedCategory == "all" || string.Equals(application.Category, _selectedCategory, StringComparison.OrdinalIgnoreCase))
            && (query.Length == 0 || application.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || application.Path.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(application => application.Name).ToArray();
        VisibleApps.Clear();
        foreach (var application in selected) VisibleApps.Add(application);
        EmptyState.Visibility = VisibleApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplicationsList.Visibility = VisibleApps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var groupLabel = _selectedCategory == "all"
            ? LocalizationService.IsEnglish ? "all applications" : "todas las aplicaciones"
            : LocalizationService.IsEnglish ? $"the '{_selectedCategory}' group" : $"el grupo '{_selectedCategory}'";
        LockGroupMenuItem.Text = LocalizationService.IsEnglish ? $"Lock {groupLabel}" : $"Bloquear {groupLabel}";
        EnableGroupMenuItem.Text = LocalizationService.IsEnglish ? $"Enable protection for {groupLabel}" : $"Activar protección de {groupLabel}";
        DisableGroupMenuItem.Text = LocalizationService.IsEnglish ? $"Disable protection for {groupLabel}" : $"Desactivar protección de {groupLabel}";
        RefreshStats();
    }

    private List<string> GetCategorySuggestions()
    {
        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "General", "Trabajo", "Juegos", "Navegadores", "Desarrollo", "Social", "Multimedia"
        };
        foreach (var application in Applications)
        {
            if (!string.IsNullOrWhiteSpace(application.Category))
                categories.Add(application.Category.Trim());
        }
        return categories.OrderBy(category => category).ToList();
    }

    private AutoSuggestBox CreateCategoryEditor(string? currentCategory)
    {
        var box = new AutoSuggestBox
        {
            Header = "Grupo o categoría",
            Text = string.IsNullOrWhiteSpace(currentCategory) ? "General" : currentCategory.Trim(),
            PlaceholderText = "General, Trabajo, Juegos, Navegadores..."
        };
        var suggestions = GetCategorySuggestions();
        box.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var query = box.Text.Trim();
            box.ItemsSource = string.IsNullOrEmpty(query)
                ? suggestions
                : suggestions.Where(category => category.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        };
        box.SuggestionChosen += (_, args) => box.Text = args.SelectedItem.ToString();
        return box;
    }

    private async void LockGroup_Click(object sender, RoutedEventArgs e)
    {
        if (!_guardianManaged)
        {
            await ShowMessageAsync("Guardian no disponible", "No se puede bloquear el grupo mientras el servicio Guardian no esté activo. Repara el servicio desde Configuración.");
            return;
        }
        var targetApps = _selectedCategory == "all" ? Applications.Where(application => application.IsEnabled).ToList()
            : Applications.Where(application => application.IsEnabled && string.Equals(application.Category, _selectedCategory, StringComparison.OrdinalIgnoreCase)).ToList();
        if (targetApps.Count == 0)
        {
            await ShowMessageAsync("Sin aplicaciones", "No hay aplicaciones activas en el grupo seleccionado.");
            return;
        }
        var groupName = _selectedCategory == "all" ? "todas las aplicaciones" : $"el grupo '{_selectedCategory}'";
        var terminated = 0;
        if (_guardianManaged)
        {
            if (_guardianToken is null && !await VerifyMasterAsync($"Autorizar bloqueo de {groupName}")) return;
            foreach (var application in targetApps)
            {
                var response = await _guardianClient.LockRuleAsync(application.Id, _guardianToken ?? string.Empty);
                if (response.Success) terminated += response.AffectedProcessCount;
                _monitor.CancelAuthorizedLaunch(application);
                _monitor.Resolve(application);
            }
        }
        AddActivity("ProtectedApp", $"Bloqueo de grupo '{_selectedCategory}' aplicado; {terminated} procesos finalizados");
        await ShowMessageAsync("Grupo bloqueado", $"Se ha revocado el acceso para {targetApps.Count} aplicaciones ({terminated} procesos finalizados).");
    }

    private async void EnableGroup_Click(object sender, RoutedEventArgs e)
    {
        var targetApps = _selectedCategory == "all" ? Applications.Where(application => !application.IsEnabled).ToList()
            : Applications.Where(application => !application.IsEnabled && string.Equals(application.Category, _selectedCategory, StringComparison.OrdinalIgnoreCase)).ToList();
        if (targetApps.Count == 0)
        {
            await ShowMessageAsync("Sin cambios", "Todas las aplicaciones del grupo ya están activas.");
            return;
        }
        foreach (var application in targetApps) application.IsEnabled = true;
        await SaveAsync();
        RefreshVisibleApps();
        var groupName = _selectedCategory == "all" ? "Todas" : _selectedCategory;
        AddActivity("ProtectedApp", $"Protección activada en lote para '{groupName}' ({targetApps.Count} aplicaciones)");
    }

    private async void DisableGroup_Click(object sender, RoutedEventArgs e)
    {
        var targetApps = _selectedCategory == "all" ? Applications.Where(application => application.IsEnabled).ToList()
            : Applications.Where(application => application.IsEnabled && string.Equals(application.Category, _selectedCategory, StringComparison.OrdinalIgnoreCase)).ToList();
        if (targetApps.Count == 0)
        {
            await ShowMessageAsync("Sin cambios", "No hay aplicaciones activas en el grupo.");
            return;
        }
        var groupName = _selectedCategory == "all" ? "todas las aplicaciones" : $"el grupo '{_selectedCategory}'";
        if (!await VerifyMasterAsync($"Desactivar protección de {groupName}")) return;
        foreach (var application in targetApps) application.IsEnabled = false;
        await SaveAsync();
        RefreshVisibleApps();
        AddActivity("ProtectedApp", $"Protección desactivada en lote para '{groupName}' ({targetApps.Count} aplicaciones)");
    }
}
