using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Station.DTOs;
using Station.Services;
using Station.ViewModels;
using Windows.UI;

namespace Station.Views
{
    public sealed partial class UserManagementPage : Page
    {
        private const string SaveCancelDialogAcceptedKey = "SaveCancelDialogAccepted";
        private const string SaveCancelDialogValidatorKey = "SaveCancelDialogValidator";

        private readonly UserApiService _userApiService = new();

        public UserManagementViewModel ViewModel => (UserManagementViewModel)DataContext;

        public UserManagementPage()
        {
            InitializeComponent();
            Loaded += UserManagementPage_Loaded;
            UpdateTabVisualState(showUsers: true);
        }

        private async void UserManagementPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= UserManagementPage_Loaded;
            await ViewModel.LoadAccessDataAsync();
            RefreshUserRoleFilterOptions();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.UserSearchText = SearchBox.Text;
        }

        private void RoleSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.RoleSearchText = RoleSearchBox.Text;
        }

        private void RoleDisplayFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem item)
            {
                ViewModel.RoleDisplayFilter = item.Tag?.ToString() ?? "all";
            }
        }

        private void RoleSortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem item)
            {
                ViewModel.RoleSortOption = item.Tag?.ToString() ?? "name_asc";
            }
        }

        private void UserRoleFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem item)
            {
                ViewModel.UserRoleFilter = item.Tag?.ToString() ?? "all";
            }
        }

        private void UserStatusFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem item)
            {
                ViewModel.UserStatusFilter = item.Tag?.ToString() ?? "all";
            }
        }

        private async void ViewAuditLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var logs = await _userApiService.GetAuditLogsAsync();
                var entries = logs.Select(MapAuditLog).ToList();

                var searchBox = CreateDialogSearchTextBox("Tìm theo thao tác, người thực hiện hoặc đối tượng...");

                var rowsPanel = new StackPanel
                {
                    Spacing = 0
                };

                void RenderLogs(string? searchText)
                {
                    rowsPanel.Children.Clear();

                    var filtered = FilterAuditLogs(entries, searchText).ToList();
                    if (filtered.Count == 0)
                    {
                        rowsPanel.Children.Add(new Border
                        {
                            Padding = new Thickness(12, 12, 12, 12),
                            Child = new TextBlock
                            {
                                Text = "Không có nhật ký phù hợp.",
                                Foreground = ThemeBrush("TextSecondaryBrush"),
                                HorizontalAlignment = HorizontalAlignment.Center,
                                TextAlignment = TextAlignment.Center
                            }
                        });
                        return;
                    }

                    foreach (var entry in filtered)
                    {
                        rowsPanel.Children.Add(CreateAuditLogRow(entry));
                    }
                }

                searchBox.TextChanged += (_, _) => RenderLogs(searchBox.Text);
                RenderLogs(string.Empty);

                var content = new StackPanel
                {
                    Spacing = 12
                };

                content.Children.Add(searchBox);
                content.Children.Add(CreateAuditLogHeaderRow());
                content.Children.Add(new Border
                {
                    BorderBrush = ThemeBrush("BorderLightBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeBrush("BackgroundPrimaryBrush"),
                    Child = new ScrollViewer
                    {
                        Content = rowsPanel,
                        Background = ThemeBrush("BackgroundPrimaryBrush"),
                        Height = 360,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollMode = ScrollMode.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        HorizontalScrollMode = ScrollMode.Disabled
                    }
                });

                var dialogBackground = ThemeBrush("BackgroundSecondaryBrush");
                var dialogBorder = ThemeBrush("BorderLightBrush");
                var dialog = new ContentDialog
                {
                    Title = "Nhật ký thao tác",
                    Content = new Border
                    {
                        Width = 960,
                        Background = dialogBackground,
                        Padding = new Thickness(18, 10, 18, 8),
                        Child = new ScrollViewer
                        {
                            Content = content,
                            Background = dialogBackground,
                            Height = 460,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                            VerticalScrollMode = ScrollMode.Auto,
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                            HorizontalScrollMode = ScrollMode.Disabled
                        }
                    },
                    PrimaryButtonText = string.Empty,
                    IsPrimaryButtonEnabled = false,
                    CloseButtonText = "Đóng",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = ThemeService.Instance.CurrentTheme
                };

                dialog.Resources["ContentDialogBackground"] = dialogBackground;
                dialog.Resources["ContentDialogBorderBrush"] = dialogBorder;
                dialog.Resources["ContentDialogBorderThemeBrush"] = dialogBorder;
                dialog.Resources["SystemControlPageBackgroundChromeLowBrush"] = dialogBackground;
                dialog.Resources["SystemControlPageBackgroundChromeMediumBrush"] = dialogBackground;
                dialog.Resources["SystemControlPageBackgroundChromeHighBrush"] = dialogBackground;
                dialog.Resources["SystemControlBackgroundBaseLowBrush"] = dialogBackground;
                dialog.Resources["SystemControlAltHighAcrylicWindowBrush"] = dialogBackground;
                dialog.Resources["ContentDialogTitleForeground"] = ThemeBrush("TextPrimaryBrush");
                dialog.PrimaryButtonStyle = null;
                dialog.CloseButtonStyle = CreateCancelDialogButtonStyle();

                await dialog.ShowAsync(ContentDialogPlacement.Popup);
            }
            catch (Exception ex)
            {
                var errorDialog = CreateInfoDialog(
                    "Không tải được nhật ký",
                    ex.Message);

                await errorDialog.ShowAsync(ContentDialogPlacement.Popup);
            }
        }

        private async void AddUser_Click(object sender, RoutedEventArgs e)
        {
            var dialog = CreateUserDialog("Thêm người dùng", null);
            await dialog.ShowAsync(ContentDialogPlacement.Popup);

            if (IsSaveCancelDialogAccepted(dialog))
            {
                var user = GetUserFromDialog(dialog);
                var password = GetPasswordFromDialog(dialog);

                try
                {
                    await ViewModel.CreateUserAsync(user, password);
                    RefreshUserRoleFilterOptions();
                }
                catch (Exception ex)
                {
                    var errorDialog = CreateInfoDialog(
                        "Không thể tạo người dùng",
                        ex.Message);

                    await errorDialog.ShowAsync(ContentDialogPlacement.Popup);
                }
            }
        }

        private async void EditUser_Click(object sender, RoutedEventArgs e)
        {
            var user = (sender as FrameworkElement)?.DataContext as UserItem
                       ?? ViewModel.SelectedUser;
            if (user == null) return;

            var dialog = CreateUserDialog("Chỉnh sửa người dùng", user);
            await dialog.ShowAsync(ContentDialogPlacement.Popup);

            if (IsSaveCancelDialogAccepted(dialog))
            {
                var updated = GetUserFromDialog(dialog);
                var newPassword = GetPasswordFromDialog(dialog);
                try
                {
                    await ViewModel.UpdateUserAccessAsync(user, updated, newPassword);
                    RefreshUserRoleFilterOptions();
                }
                catch (Exception ex)
                {
                    var errorDialog = CreateInfoDialog(
                        "Không thể cập nhật người dùng",
                        ex.Message);

                    await errorDialog.ShowAsync(ContentDialogPlacement.Popup);
                }
            }
        }

        private async void DeleteUser_Click(object sender, RoutedEventArgs e)
        {
            var user = (sender as FrameworkElement)?.DataContext as UserItem
                       ?? ViewModel.SelectedUser;
            if (user == null) return;

            var confirm = CreateConfirmDialog(
                "Xóa người dùng",
                $"Tài khoản '{user.UserName}' sẽ bị xóa khỏi danh sách quản trị.",
                "Xóa");

            var result = await confirm.ShowAsync(ContentDialogPlacement.Popup);
            if (result == ContentDialogResult.Primary)
            {
                await ViewModel.DeleteUserAsync(user);
                RefreshUserRoleFilterOptions();
            }
        }

        private ContentDialog CreateUserDialog(string title, UserItem? existing)
        {
            var isCreateMode = existing == null;

            var userNameBox = CreateTextBox(
                "Tài khoản",
                existing?.UserName ?? string.Empty,
                "Nhập tên tài khoản");

            var fullNameBox = CreateTextBox(
                "Họ tên",
                existing?.FullName ?? string.Empty,
                "Nhập họ tên");

            var passwordBox = CreatePasswordBox(
                isCreateMode ? "Mật khẩu" : "Mật khẩu mới",
                isCreateMode ? "Nhập mật khẩu đăng nhập" : "Để trống nếu không thay đổi");

            var roleBox = CreateComboBox(
                "Vai trò",
                ViewModel.Roles.Select(r => r.Name).ToArray(),
                existing?.Role.Split(", ").FirstOrDefault() ?? ViewModel.Roles.FirstOrDefault()?.Name ?? "Viewer");

            var activeSwitch = new ToggleSwitch
            {
                Header = "Cho phép đăng nhập",
                IsOn = existing?.IsActive ?? true,
                OnContent = "Hoạt động",
                OffContent = "Khóa",
                Margin = new Thickness(0, 4, 0, 0)
            };

            activeSwitch.Resources["ToggleSwitchHeaderForeground"] = WhiteTextBrush();
            activeSwitch.Resources["ToggleSwitchForeground"] = WhiteTextBrush();

            var formGrid = new Grid
            {
                ColumnSpacing = 16,
                RowSpacing = 16
            };

            formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetColumn(userNameBox, 0);
            Grid.SetRow(userNameBox, 0);

            Grid.SetColumn(fullNameBox, 1);
            Grid.SetRow(fullNameBox, 0);

            Grid.SetColumn(passwordBox, 0);
            Grid.SetRow(passwordBox, 1);
            Grid.SetColumnSpan(passwordBox, 2);

            Grid.SetColumn(roleBox, 0);
            Grid.SetRow(roleBox, 2);
            Grid.SetColumnSpan(roleBox, 2);

            Grid.SetColumn(activeSwitch, 0);
            Grid.SetRow(activeSwitch, 3);
            Grid.SetColumnSpan(activeSwitch, 2);

            formGrid.Children.Add(userNameBox);
            formGrid.Children.Add(fullNameBox);
            formGrid.Children.Add(passwordBox);
            formGrid.Children.Add(roleBox);
            formGrid.Children.Add(activeSwitch);

            var content = new StackPanel
            {
                Spacing = 12,
                Padding = new Thickness(2, 0, 2, 0)
            };
            content.Children.Add(formGrid);

            var dialog = CreateStyledDialog(
                title,
                content,
                new DialogControls(userNameBox, fullNameBox, passwordBox, roleBox, activeSwitch, isCreateMode),
                700,
                360);

            dialog.Resources[SaveCancelDialogValidatorKey] = new Func<ContentDialog, bool>(ValidateUserDialog);

            return dialog;
        }

        private UserItem GetUserFromDialog(ContentDialog dialog)
        {
            var controls = (DialogControls)dialog.Tag;

            return new UserItem
            {
                UserName = controls.UserNameBox.Text.Trim(),
                FullName = controls.FullNameBox.Text.Trim(),
                Role = controls.RoleBox.SelectedItem?.ToString() ?? "Viewer",
                IsActive = controls.ActiveSwitch.IsOn
            };
        }

        private string GetPasswordFromDialog(ContentDialog dialog)
        {
            var controls = (DialogControls)dialog.Tag;
            return controls.PasswordBox.Password?.Trim() ?? string.Empty;
        }

        private bool ValidateUserDialog(ContentDialog dialog)
        {
            var controls = (DialogControls)dialog.Tag;

            if (string.IsNullOrWhiteSpace(controls.UserNameBox.Text))
            {
                controls.UserNameBox.Focus(FocusState.Programmatic);
                return false;
            }

            if (string.IsNullOrWhiteSpace(controls.FullNameBox.Text))
            {
                controls.FullNameBox.Focus(FocusState.Programmatic);
                return false;
            }

            if (controls.IsCreateMode && string.IsNullOrWhiteSpace(controls.PasswordBox.Password))
            {
                controls.PasswordBox.Focus(FocusState.Programmatic);
                return false;
            }

            if (!controls.IsCreateMode &&
                !string.IsNullOrWhiteSpace(controls.PasswordBox.Password) &&
                controls.PasswordBox.Password.Trim().Length < 6)
            {
                controls.PasswordBox.Focus(FocusState.Programmatic);
                return false;
            }

            return true;
        }

        private record DialogControls(
            TextBox UserNameBox,
            TextBox FullNameBox,
            PasswordBox PasswordBox,
            ComboBox RoleBox,
            ToggleSwitch ActiveSwitch,
            bool IsCreateMode);

        /// <summary>Two-letter monogram for the user avatar (never a generic person glyph).</summary>
        public static string Initials(string fullName, string userName)
        {
            var source = string.IsNullOrWhiteSpace(fullName) ? userName : fullName;
            var parts = (source ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            if (parts.Length == 1)
                return parts[0].Length >= 2 ? parts[0][..2].ToUpperInvariant() : parts[0].ToUpperInvariant();
            return (parts[0][..1] + parts[^1][..1]).ToUpperInvariant();
        }

        /// <summary>Filled circle = active, hollow ring = locked.</summary>
        public static string StatusMarker(bool isActive) => isActive ? "●" : "○";

        public static Brush StatusBrushFor(bool isActive)
            => (Brush)Station.Services.ThemeBrushes.Get(isActive ? "DkGreenBrush" : "DkGrayBrush");

        public static Visibility EmptyVisibility(int count)
            => count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void UsersTabButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateTabVisualState(showUsers: true);
        }

        private void RolesTabButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateTabVisualState(showUsers: false);
        }

        private void UpdateTabVisualState(bool showUsers)
        {
            UsersSection.Visibility = showUsers ? Visibility.Visible : Visibility.Collapsed;
            RolesSection.Visibility = showUsers ? Visibility.Collapsed : Visibility.Visible;

            var primary = BrushResource("DkBlueBgBrush");
            var activeText = BrushResource("DkBlueLightBrush");
            var bgPrimary = new SolidColorBrush(Colors.Transparent);
            var textSecondary = BrushResource("DkTextSecondaryBrush");

            if (showUsers)
            {
                UsersTabButton.Background = primary;
                UsersTabButton.Foreground = activeText;

                RolesTabButton.Background = bgPrimary;
                RolesTabButton.Foreground = textSecondary;
            }
            else
            {
                RolesTabButton.Background = primary;
                RolesTabButton.Foreground = activeText;

                UsersTabButton.Background = bgPrimary;
                UsersTabButton.Foreground = textSecondary;
            }
        }

        private record RoleDialogControls(
            TextBox NameBox,
            TextBox DescriptionBox,
            ToggleSwitch IsSystemSwitch,
            Panel PermissionsPanel,
            int ExistingUsersCount);

        private ContentDialog CreateRoleDialog(string title, RoleItem? existing)
        {
            var nameBox = CreateTextBox(
                "Tên vai trò",
                existing?.Name ?? string.Empty,
                "Nhập tên vai trò");

            var descriptionBox = CreateTextBox(
                "Mã vai trò",
                existing?.Code ?? existing?.Description ?? string.Empty,
                "VD: VIEWER_ROLE");

            var isSystemSwitch = new ToggleSwitch
            {
                Header = "Vai trò hệ thống",
                IsOn = existing?.IsSystem ?? true,
                OnContent = "Hệ thống",
                OffContent = "Tùy chỉnh",
                Margin = new Thickness(0, 4, 0, 0)
            };

            isSystemSwitch.Resources["ToggleSwitchHeaderForeground"] = WhiteTextBrush();
            isSystemSwitch.Resources["ToggleSwitchForeground"] = WhiteTextBrush();

            var permissionPanel = CreatePermissionGroupsTwoColumnPanel(existing);

            var leftPanel = new StackPanel
            {
                Spacing = 14
            };
            leftPanel.Children.Add(nameBox);
            leftPanel.Children.Add(descriptionBox);
            leftPanel.Children.Add(isSystemSwitch);

            var rightPanel = new StackPanel
            {
                Spacing = 14
            };

            rightPanel.Children.Add(new TextBlock
            {
                Text = "Phân quyền chức năng",
                Foreground = WhiteTextBrush(),
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });

            var permissionSection = new Border
            {
                Background = TransparentBrush(),
                BorderBrush = DialogInputBorderBrush(),
                
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12),
                Height = 180,
                Child = new ScrollViewer
                {
                    Content = permissionPanel,
                    Background = TransparentBrush(),
                    Padding = new Thickness(0),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalScrollMode = ScrollMode.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollMode = ScrollMode.Auto
                }
            };
            rightPanel.Children.Add(permissionSection);

            var layoutGrid = new Grid
            {
                ColumnSpacing = 14,
                RowSpacing = 0
            };

            layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
            layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            Grid.SetColumn(leftPanel, 0);
            Grid.SetColumn(rightPanel, 1);
            layoutGrid.Children.Add(leftPanel);
            layoutGrid.Children.Add(rightPanel);

            var content = new StackPanel
            {
                Spacing = 12,
                Padding = new Thickness(2, 0, 2, 0)
            };
            content.Children.Add(layoutGrid);

            return CreateStyledDialog(
                title,
                content,
                new RoleDialogControls(
                    nameBox,
                    descriptionBox,
                    isSystemSwitch,
                    permissionPanel,
                    existing?.UsersCount ?? 0),
                880,
                280);
        }
        private RoleItem GetRoleFromDialog(ContentDialog dialog)
        {
            var controls = (RoleDialogControls)dialog.Tag;
            var roleCode = controls.DescriptionBox.Text.Trim().ToUpperInvariant();

            var role = new RoleItem
            {
                Code = roleCode,
                Name = controls.NameBox.Text.Trim(),
                Description = roleCode,
                IsSystem = controls.IsSystemSwitch.IsOn,
                UsersCount = controls.ExistingUsersCount
            };

            role.Permissions.Clear();
            foreach (var permissionCode in EnumeratePermissionCheckBoxes(controls.PermissionsPanel)
                .Where(checkBox => checkBox.IsChecked == true)
                .Select(checkBox => checkBox.Tag as string)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                role.Permissions.Add(permissionCode!);
            }

            role.PermissionsCount = role.Permissions.Count;

            return role;
        }

        private async void AddRole_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = CreateRoleDialog("Tạo vai trò", null);
                await dialog.ShowAsync(ContentDialogPlacement.Popup);

                if (IsSaveCancelDialogAccepted(dialog))
                {
                    var role = GetRoleFromDialog(dialog);
                    await ViewModel.CreateRoleAsync(role);
                    RefreshUserRoleFilterOptions();
                }
            }
            catch (Exception ex)
            {
                var errorDialog = CreateInfoDialog(
                    "Không thể mở biểu mẫu vai trò",
                    ex.Message);

                await errorDialog.ShowAsync(ContentDialogPlacement.Popup);
            }
        }

        private async void EditRole_Click(object sender, RoutedEventArgs e)
        {
            var role = (sender as FrameworkElement)?.DataContext as RoleItem;
            if (role == null) return;

            var dialog = CreateRoleDialog("Chỉnh sửa vai trò", role);
            await dialog.ShowAsync(ContentDialogPlacement.Popup);

            if (IsSaveCancelDialogAccepted(dialog))
            {
                var updated = GetRoleFromDialog(dialog);
                await ViewModel.UpdateRoleAsync(role, updated);
                RefreshUserRoleFilterOptions();
            }
        }

        private async void DeleteRole_Click(object sender, RoutedEventArgs e)
        {
            var role = (sender as FrameworkElement)?.DataContext as RoleItem;
            if (role == null) return;

            var confirm = CreateConfirmDialog(
                "Xóa vai trò",
                $"Vai trò '{role.Name}' sẽ bị xóa khỏi danh sách phân quyền.",
                "Xóa");

            var result = await confirm.ShowAsync(ContentDialogPlacement.Popup);
            if (result == ContentDialogResult.Primary)
            {
                await ViewModel.DeleteRoleAsync(role);
                RefreshUserRoleFilterOptions();
            }
        }

        private void RefreshUserRoleFilterOptions()
        {
            var selectedTag = (UserRoleFilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
            var roleNames = ViewModel.Users
                .SelectMany(user => user.Role.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries))
                .Where(role => !string.IsNullOrWhiteSpace(role) && !string.Equals(role, "No role", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
                .ToList();

            UserRoleFilterBox.Items.Clear();
            UserRoleFilterBox.Items.Add(new ComboBoxItem
            {
                Content = "Vai trò: Tất cả",
                Tag = "all"
            });

            foreach (var roleName in roleNames)
            {
                UserRoleFilterBox.Items.Add(new ComboBoxItem
                {
                    Content = roleName,
                    Tag = roleName
                });
            }

            UserRoleFilterBox.Items.Add(new ComboBoxItem
            {
                Content = "Chưa gán vai trò",
                Tag = "unassigned"
            });

            var selectedItem = UserRoleFilterBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), selectedTag, StringComparison.OrdinalIgnoreCase))
                ?? UserRoleFilterBox.Items.OfType<ComboBoxItem>().FirstOrDefault();

            UserRoleFilterBox.SelectedItem = selectedItem;
        }

        private ContentDialog CreateStyledDialog(string title, UIElement content, object tag, double width, double height = 460)
        {
            var dialogBackground = SurfaceBlueBrush();
            var dialogBorder = BorderSoftBrush();
            var primaryButtonBrush = new SolidColorBrush(Color.FromArgb(255, 37, 99, 235));
            var primaryButtonHoverBrush = new SolidColorBrush(Color.FromArgb(255, 59, 130, 246));
            var primaryButtonPressedBrush = new SolidColorBrush(Color.FromArgb(255, 29, 78, 216));
            var primaryButtonForegroundBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));

            var scrollViewer = new ScrollViewer
            {
                Content = content,
                Background = TransparentBrush(),
                Height = height,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Disabled,
                Padding = new Thickness(0)
            };

            var contentHost = new Border
            {
                Width = width,
                Height = height,
                Background = TransparentBrush(),
                Padding = new Thickness(18, 10, 18, 8),
                Child = scrollViewer
            };

            var dialogContent = new StackPanel
            {
                Spacing = 14,
                Width = width
            };
            dialogContent.Children.Add(contentHost);

            var dialog = new ContentDialog
            {
                Title = title,
                Content = dialogContent,
                XamlRoot = this.XamlRoot,
                Tag = tag,
                RequestedTheme = ThemeService.Instance.CurrentTheme
            };

            dialog.Resources[SaveCancelDialogAcceptedKey] = false;
            dialogContent.Children.Add(CreateSaveCancelActionBar(dialog, width));

            dialog.Resources["ContentDialogBackground"] = dialogBackground;
            dialog.Resources["ContentDialogBorderBrush"] = dialogBorder;
            dialog.Resources["ContentDialogBorderThemeBrush"] = dialogBorder;
            dialog.Resources["SystemControlPageBackgroundChromeLowBrush"] = dialogBackground;
            dialog.Resources["SystemControlPageBackgroundChromeMediumBrush"] = dialogBackground;
            dialog.Resources["SystemControlPageBackgroundChromeHighBrush"] = dialogBackground;
            dialog.Resources["SystemControlBackgroundBaseLowBrush"] = dialogBackground;
            dialog.Resources["SystemControlAltHighAcrylicWindowBrush"] = dialogBackground;
            dialog.Resources["ContentDialogTitleForeground"] = WhiteTextBrush();
            dialog.Resources["ContentDialogButtonPrimaryBackground"] = primaryButtonBrush;
            dialog.Resources["ContentDialogButtonPrimaryBackgroundPointerOver"] = primaryButtonHoverBrush;
            dialog.Resources["ContentDialogButtonPrimaryBackgroundPressed"] = primaryButtonPressedBrush;
            dialog.Resources["ContentDialogButtonPrimaryBorderBrush"] = dialogBorder;
            dialog.Resources["ContentDialogButtonPrimaryBorderBrushPointerOver"] = dialogBorder;
            dialog.Resources["ContentDialogButtonPrimaryBorderBrushPressed"] = dialogBorder;
            dialog.Resources["ContentDialogButtonPrimaryForeground"] = primaryButtonForegroundBrush;
            dialog.Resources["ContentDialogButtonPrimaryForegroundPointerOver"] = primaryButtonForegroundBrush;
            dialog.Resources["ContentDialogButtonPrimaryForegroundPressed"] = primaryButtonForegroundBrush;
            dialog.Resources["AccentButtonBackground"] = primaryButtonBrush;
            dialog.Resources["AccentButtonBackgroundPointerOver"] = primaryButtonHoverBrush;
            dialog.Resources["AccentButtonBackgroundPressed"] = primaryButtonPressedBrush;
            dialog.Resources["AccentButtonBorderBrush"] = dialogBorder;
            dialog.Resources["AccentButtonBorderBrushPointerOver"] = dialogBorder;
            dialog.Resources["AccentButtonBorderBrushPressed"] = dialogBorder;
            dialog.Resources["AccentButtonForeground"] = primaryButtonForegroundBrush;
            dialog.Resources["AccentButtonForegroundPointerOver"] = primaryButtonForegroundBrush;
            dialog.Resources["AccentButtonForegroundPressed"] = primaryButtonForegroundBrush;

            return dialog;
        }

        private StackPanel CreateSaveCancelActionBar(ContentDialog dialog, double width)
        {
            const double buttonSpacing = 14;
            var buttonWidth = Math.Floor((width - buttonSpacing) / 2);

            var actionBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Width = buttonWidth * 2 + buttonSpacing,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0)
            };

            var saveButton = CreateDialogActionSurface(
                "Lưu",
                buttonWidth,
                new SolidColorBrush(Color.FromArgb(255, 37, 99, 235)),
                new SolidColorBrush(Color.FromArgb(255, 59, 130, 246)),
                new SolidColorBrush(Color.FromArgb(255, 29, 78, 216)),
                ThemeBrush("BorderLightBrush"),
                new SolidColorBrush(Colors.White));

            var cancelButton = CreateDialogActionSurface(
                "Hủy",
                buttonWidth,
                ThemeBrush("BackgroundSecondaryBrush"),
                ThemeBrush("BackgroundSecondaryBrush"),
                ThemeBrush("BackgroundSecondaryBrush"),
                ThemeBrush("BorderLightBrush"),
                ThemeBrush("TextPrimaryBrush"));

            saveButton.Margin = new Thickness(0, 0, buttonSpacing, 0);

            saveButton.Tapped += (_, _) =>
            {
                if (dialog.Resources.TryGetValue(SaveCancelDialogValidatorKey, out var validator)
                    && validator is Func<ContentDialog, bool> validate
                    && !validate(dialog))
                {
                    return;
                }

                dialog.Resources[SaveCancelDialogAcceptedKey] = true;
                dialog.Hide();
            };

            cancelButton.Tapped += (_, _) =>
            {
                dialog.Resources[SaveCancelDialogAcceptedKey] = false;
                dialog.Hide();
            };

            actionBar.Children.Add(saveButton);
            actionBar.Children.Add(cancelButton);

            return actionBar;
        }

        private Border CreateDialogActionSurface(
            string text,
            double width,
            Brush background,
            Brush pointerOverBackground,
            Brush pressedBackground,
            Brush borderBrush,
            Brush foreground)
        {
            var surface = new Border
            {
                Width = width,
                Height = 44,
                MinHeight = 44,
                MaxHeight = 44,
                MinWidth = width,
                MaxWidth = width,
                Margin = new Thickness(0),
                Background = background,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = foreground,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
            };

            surface.PointerEntered += (_, _) => surface.Background = pointerOverBackground;
            surface.PointerExited += (_, _) => surface.Background = background;
            surface.PointerPressed += (_, _) => surface.Background = pressedBackground;
            surface.PointerReleased += (_, _) => surface.Background = pointerOverBackground;

            return surface;
        }

        private static bool IsSaveCancelDialogAccepted(ContentDialog dialog)
        {
            return dialog.Resources.TryGetValue(SaveCancelDialogAcceptedKey, out var accepted)
                && accepted is bool isAccepted
                && isAccepted;
        }

        private ContentDialog CreateConfirmDialog(string title, string message, string actionText)
        {
            var dialogBackground = DialogBlueBrush();
            var dialogBorder = BorderSoftBrush();

            var content = new Border
            {
                Width = 440,
                Background = dialogBackground,
                Padding = new Thickness(24, 16, 24, 12),
                Child = CreateDialogShell(message)
            };

            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = actionText,
                SecondaryButtonText = "Hủy",
                DefaultButton = ContentDialogButton.Secondary,
                XamlRoot = this.XamlRoot,
                RequestedTheme = ThemeService.Instance.CurrentTheme
            };

            dialog.Resources["ContentDialogBackground"] = dialogBackground;
            dialog.Resources["ContentDialogBorderBrush"] = dialogBorder;
            dialog.Resources["ContentDialogBorderThemeBrush"] = dialogBorder;
            dialog.Resources["SystemControlPageBackgroundChromeLowBrush"] = dialogBackground;
            dialog.Resources["SystemControlPageBackgroundChromeMediumBrush"] = dialogBackground;
            dialog.Resources["SystemControlPageBackgroundChromeHighBrush"] = dialogBackground;
            dialog.Resources["SystemControlBackgroundBaseLowBrush"] = dialogBackground;
            dialog.Resources["SystemControlAltHighAcrylicWindowBrush"] = dialogBackground;
            dialog.Resources["ContentDialogTitleForeground"] = WhiteTextBrush();

            dialog.PrimaryButtonStyle = CreatePrimaryDialogButtonStyle();
            dialog.SecondaryButtonStyle = CreateCancelDialogButtonStyle();
            dialog.Opened += (_, _) =>
            {
                NormalizeDialogActionButtons(dialog);
                DispatcherQueue.TryEnqueue(() => NormalizeDialogActionButtons(dialog));
            };

            return dialog;
        }

        private ContentDialog CreateInfoDialog(string title, string message)
        {
            var dialogBackground = DialogBlueBrush();
            var dialogBorder = BorderSoftBrush();

            var content = new Border
            {
                Width = 440,
                Background = dialogBackground,
                Padding = new Thickness(24, 16, 24, 12),
                Child = CreateDialogShell(message)
            };

            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "Đóng",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
                RequestedTheme = ThemeService.Instance.CurrentTheme
            };

            dialog.Resources["ContentDialogBackground"] = dialogBackground;
            dialog.Resources["ContentDialogBorderBrush"] = dialogBorder;
            dialog.Resources["ContentDialogBorderThemeBrush"] = dialogBorder;
            dialog.Resources["SystemControlPageBackgroundChromeLowBrush"] = dialogBackground;
            dialog.Resources["SystemControlPageBackgroundChromeMediumBrush"] = dialogBackground;
            dialog.Resources["SystemControlPageBackgroundChromeHighBrush"] = dialogBackground;
            dialog.Resources["SystemControlBackgroundBaseLowBrush"] = dialogBackground;
            dialog.Resources["SystemControlAltHighAcrylicWindowBrush"] = dialogBackground;
            dialog.Resources["ContentDialogTitleForeground"] = WhiteTextBrush();
            dialog.CloseButtonStyle = CreateCancelDialogButtonStyle();

            return dialog;
        }

        private StackPanel CreateDialogShell(string message)
        {
            var panel = new StackPanel
            {
                Spacing = 14,
                Padding = new Thickness(2, 0, 2, 0)
            };

            panel.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = MutedTextBrush(),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            });

            return panel;
        }

        private TextBox CreateTextBox(string label, string text, string placeholder, bool acceptsReturn = false)
        {
            var normalBackground = DialogInputBackgroundBrush();
            var focusedBackground = DialogInputBackgroundBrush();
            var pointerBackground = DialogInputBackgroundBrush();
            var normalBorder = DialogInputBorderBrush();
            var focusedBorder = DialogInputBorderFocusedBrush();
            var pointerBorder = DialogInputBorderFocusedBrush();
            var normalForeground = DialogInputForegroundBrush();
            var focusedForeground = DialogInputForegroundBrush();
            var placeholderBrush = DialogInputPlaceholderBrush();
            var focusedPlaceholderBrush = DialogInputPlaceholderBrush();

            var textBox = new TextBox
            {
                Style = AppStyle("StandardTextBoxStyle"),
                Header = label,
                Text = text,
                PlaceholderText = placeholder,
                AcceptsReturn = acceptsReturn,
                TextWrapping = acceptsReturn ? TextWrapping.Wrap : TextWrapping.NoWrap,
                MinHeight = acceptsReturn ? 104 : 40,
                Background = normalBackground,
                BorderBrush = normalBorder,
                Foreground = normalForeground,
                PlaceholderForeground = placeholderBrush,
                CornerRadius = new CornerRadius(8),
                Padding = acceptsReturn ? new Thickness(12, 10, 12, 10) : new Thickness(12, 10, 12, 10),
                BorderThickness = new Thickness(1)
            };

            textBox.Resources["TextControlBackground"] = normalBackground;
            textBox.Resources["TextControlBackgroundPointerOver"] = pointerBackground;
            textBox.Resources["TextControlBackgroundPressed"] = pointerBackground;
            textBox.Resources["TextControlBackgroundFocused"] = focusedBackground;
             textBox.Resources["TextControlBorderBrush"] = normalBorder;
             textBox.Resources["TextControlBorderBrushPointerOver"] = pointerBorder;
             textBox.Resources["TextControlBorderBrushPressed"] = focusedBorder;
             textBox.Resources["TextControlBorderBrushFocused"] = focusedBorder;
             textBox.Resources["TextControlBorderThemeThickness"] = new Thickness(1);
             textBox.Resources["TextControlBorderThemeThicknessPointerOver"] = new Thickness(1);
             textBox.Resources["TextControlBorderThemeThicknessPressed"] = new Thickness(1);
             textBox.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(1);
             textBox.Resources["TextControlForeground"] = normalForeground;
            textBox.Resources["TextControlForegroundPointerOver"] = normalForeground;
            textBox.Resources["TextControlForegroundPressed"] = normalForeground;
            textBox.Resources["TextControlForegroundFocused"] = focusedForeground;
            textBox.Resources["TextControlPlaceholderForeground"] = placeholderBrush;
            textBox.Resources["TextControlPlaceholderForegroundPointerOver"] = placeholderBrush;
            textBox.Resources["TextControlPlaceholderForegroundFocused"] = focusedPlaceholderBrush;
            textBox.Resources["TextControlHeaderForeground"] = DialogInputHeaderBrush();

            return textBox;
        }

        private TextBox CreateDialogSearchTextBox(string placeholder)
        {
            var normalBackground = DialogInputBackgroundBrush();
            var focusedBackground = DialogInputBackgroundBrush();
            var pointerBackground = DialogInputBackgroundBrush();
            var normalBorder = DialogInputBorderBrush();
            var focusedBorder = DialogInputBorderFocusedBrush();
            var pointerBorder = DialogInputBorderFocusedBrush();
            var foreground = DialogInputForegroundBrush();
            var placeholderBrush = DialogInputPlaceholderBrush();

            var textBox = new TextBox
            {
                Style = AppStyle("StandardTextBoxStyle"),
                PlaceholderText = placeholder,
                MinHeight = 40,
                Background = normalBackground,
                BorderBrush = normalBorder,
                Foreground = foreground,
                PlaceholderForeground = placeholderBrush,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                BorderThickness = new Thickness(1)
            };

            textBox.Resources["TextControlBackground"] = normalBackground;
            textBox.Resources["TextControlBackgroundPointerOver"] = pointerBackground;
            textBox.Resources["TextControlBackgroundPressed"] = pointerBackground;
            textBox.Resources["TextControlBackgroundFocused"] = focusedBackground;
            textBox.Resources["TextControlBorderBrush"] = normalBorder;
            textBox.Resources["TextControlBorderBrushPointerOver"] = pointerBorder;
            textBox.Resources["TextControlBorderBrushPressed"] = focusedBorder;
            textBox.Resources["TextControlBorderBrushFocused"] = focusedBorder;
            textBox.Resources["TextControlBorderThemeThickness"] = new Thickness(1);
            textBox.Resources["TextControlBorderThemeThicknessPointerOver"] = new Thickness(1);
            textBox.Resources["TextControlBorderThemeThicknessPressed"] = new Thickness(1);
            textBox.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(1);
            textBox.Resources["TextControlForeground"] = foreground;
            textBox.Resources["TextControlForegroundPointerOver"] = foreground;
            textBox.Resources["TextControlForegroundPressed"] = foreground;
            textBox.Resources["TextControlForegroundFocused"] = foreground;
            textBox.Resources["TextControlPlaceholderForeground"] = placeholderBrush;
            textBox.Resources["TextControlPlaceholderForegroundPointerOver"] = placeholderBrush;
            textBox.Resources["TextControlPlaceholderForegroundFocused"] = placeholderBrush;

            return textBox;
        }

        private PasswordBox CreatePasswordBox(string label, string placeholder)
        {
            var normalBackground = DialogInputBackgroundBrush();
            var focusedBackground = DialogInputBackgroundBrush();
            var pointerBackground = DialogInputBackgroundBrush();
            var normalBorder = DialogInputBorderBrush();
            var focusedBorder = DialogInputBorderFocusedBrush();
            var pointerBorder = DialogInputBorderFocusedBrush();
            var foreground = DialogInputForegroundBrush();
             var placeholderForeground = DialogInputPlaceholderBrush();
             var header = DialogInputHeaderBrush();
             var buttonForeground = DialogInputForegroundBrush();
             var buttonBackground = DialogInputBackgroundBrush();
             var buttonBorder = DialogInputBorderBrush();
             var buttonBorderFocused = DialogInputBorderFocusedBrush();

            var passwordBox = new PasswordBox
            {
                Header = label,
                PlaceholderText = placeholder,
                MinHeight = 40,
                Background = normalBackground,
                BorderBrush = normalBorder,
                Foreground = foreground,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                BorderThickness = new Thickness(1),
                PasswordRevealMode = PasswordRevealMode.Peek
            };

            passwordBox.Resources["PasswordBoxBackground"] = normalBackground;
            passwordBox.Resources["PasswordBoxBackgroundPointerOver"] = pointerBackground;
            passwordBox.Resources["PasswordBoxBackgroundPressed"] = pointerBackground;
            passwordBox.Resources["PasswordBoxBackgroundFocused"] = focusedBackground;
             passwordBox.Resources["PasswordBoxBorderBrush"] = normalBorder;
             passwordBox.Resources["PasswordBoxBorderBrushPointerOver"] = pointerBorder;
             passwordBox.Resources["PasswordBoxBorderBrushPressed"] = focusedBorder;
             passwordBox.Resources["PasswordBoxBorderBrushFocused"] = focusedBorder;
             passwordBox.Resources["TextControlBorderThemeThickness"] = new Thickness(1);
             passwordBox.Resources["TextControlBorderThemeThicknessPointerOver"] = new Thickness(1);
             passwordBox.Resources["TextControlBorderThemeThicknessPressed"] = new Thickness(1);
             passwordBox.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(1);
             passwordBox.Resources["PasswordBoxForeground"] = foreground;
            passwordBox.Resources["PasswordBoxForegroundPointerOver"] = foreground;
            passwordBox.Resources["PasswordBoxForegroundFocused"] = foreground;
            passwordBox.Resources["PasswordBoxPlaceholderForeground"] = placeholderForeground;
            passwordBox.Resources["PasswordBoxPlaceholderForegroundPointerOver"] = placeholderForeground;
            passwordBox.Resources["PasswordBoxPlaceholderForegroundFocused"] = placeholderForeground;
            passwordBox.Resources["TextControlHeaderForeground"] = header;
            passwordBox.Resources["ButtonForeground"] = buttonForeground;
            passwordBox.Resources["ButtonForegroundPointerOver"] = buttonForeground;
            passwordBox.Resources["ButtonForegroundPressed"] = buttonForeground;
            passwordBox.Resources["ButtonBackground"] = buttonBackground;
            passwordBox.Resources["ButtonBackgroundPointerOver"] = buttonBackground;
            passwordBox.Resources["ButtonBackgroundPressed"] = buttonBackground;
            passwordBox.Resources["ButtonBorderBrush"] = buttonBorder;
            passwordBox.Resources["ButtonBorderBrushPointerOver"] = buttonBorderFocused;
            passwordBox.Resources["ButtonBorderBrushPressed"] = buttonBorderFocused;

            return passwordBox;
        }

        private ComboBox CreateComboBox(string label, string[] items, string selected)
        {
            var normalBackground = DialogInputBackgroundBrush();
            var focusedBackground = DialogInputBackgroundBrush();
            var pointerBackground = DialogInputBackgroundBrush();
            var normalBorder = DialogInputBorderBrush();
            var focusedBorder = DialogInputBorderFocusedBrush();
            var pointerBorder = DialogInputBorderFocusedBrush();
            var foreground = DialogInputForegroundBrush();
            var headerForeground = DialogInputHeaderBrush();

            var comboBox = new ComboBox
            {
                Style = AppStyle("StandardComboBoxStyle"),
                Header = label,
                ItemsSource = items,
                SelectedItem = items.Contains(selected) ? selected : items.FirstOrDefault(),
                PlaceholderText = "Chọn vai trò",
                MinHeight = 40,
                MaxDropDownHeight = 200,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = normalBackground,
                BorderBrush = normalBorder,
                Foreground = foreground,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                BorderThickness = new Thickness(1)
            };

            comboBox.Resources["ComboBoxBackground"] = normalBackground;
            comboBox.Resources["ComboBoxBackgroundPointerOver"] = pointerBackground;
            comboBox.Resources["ComboBoxBackgroundPressed"] = pointerBackground;
            comboBox.Resources["ComboBoxBackgroundFocused"] = focusedBackground;
            comboBox.Resources["ComboBoxDropDownBackground"] = DialogInputBackgroundBrush();
            comboBox.Resources["ComboBoxBorderBrush"] = normalBorder;
            comboBox.Resources["ComboBoxBorderBrushPointerOver"] = pointerBorder;
            comboBox.Resources["ComboBoxBorderBrushPressed"] = focusedBorder;
            comboBox.Resources["ComboBoxBorderBrushFocused"] = focusedBorder;
            comboBox.Resources["ComboBoxForeground"] = foreground;
            comboBox.Resources["ComboBoxHeaderForeground"] = headerForeground;

            return comboBox;
        }
     
        private StackPanel CreatePermissionGroupsTwoColumnPanel(RoleItem? existing)
        {
            var selected = existing?.Permissions
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var groups = PermissionCatalog.All.ToList();

            var leftColumn = new StackPanel
            {
                Spacing = 10
            };

            var rightColumn = new StackPanel
            {
                Spacing = 10
            };

            var leftWeight = 0;
            var rightWeight = 0;

            foreach (var group in groups)
            {
                var checkBox = new CheckBox
                {
                    Content = new TextBlock
                    {
                        Text = group.GroupName,
                        Foreground = WhiteTextBrush(),
                        FontSize = 13,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    },
                    Tag = group.Code,
                    IsChecked = selected.Contains(group.Code),
                    Foreground = WhiteTextBrush(),
                    Background = TransparentBrush(),
                    Padding = new Thickness(8, 10, 10, 10),
                    MinHeight = 44,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left
                };

                checkBox.Resources["CheckBoxBorderBrushUnchecked"] = BorderSoftBrush();
                checkBox.Resources["CheckBoxBorderBrushUncheckedPointerOver"] = BorderStrongBrush();
                checkBox.Resources["CheckBoxBorderBrushUncheckedPressed"] = BorderStrongBrush();
                checkBox.Resources["CheckBoxBorderBrushChecked"] = BorderSoftBrush();
                checkBox.Resources["CheckBoxCheckBackgroundFillChecked"] = WhiteTextBrush();
                checkBox.Resources["CheckBoxCheckGlyphForegroundChecked"] = DialogBlueBrush();

               
                const int weight = 1;

                if (leftWeight <= rightWeight)
                {
                    leftColumn.Children.Add(checkBox);
                    leftWeight += weight;
                }
                else
                {
                    rightColumn.Children.Add(checkBox);
                    rightWeight += weight;
                }
            }

            var grid = new Grid
            {
                ColumnSpacing = 12
            };

            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });

            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });

            Grid.SetColumn(leftColumn, 0);
            Grid.SetColumn(rightColumn, 1);

            grid.Children.Add(leftColumn);
            grid.Children.Add(rightColumn);

            return new StackPanel
            {
                Spacing = 5,
                Children =
        {
            grid
        }
            };
        }
       
        private IEnumerable<CheckBox> EnumeratePermissionCheckBoxes(UIElement? element)
        {
            if (element == null)
            {
                yield break;
            }

            if (element is CheckBox checkBox)
            {
                yield return checkBox;
                yield break;
            }

            if (element is Panel panel)
            {
                foreach (var child in panel.Children.OfType<UIElement>())
                {
                    foreach (var nested in EnumeratePermissionCheckBoxes(child))
                    {
                        yield return nested;
                    }
                }
                yield break;
            }

            if (element is Border border)
            {
                foreach (var nested in EnumeratePermissionCheckBoxes(border.Child))
                {
                    yield return nested;
                }
                yield break;
            }

            if (element is Expander expander)
            {
                foreach (var nested in EnumeratePermissionCheckBoxes(expander.Content as UIElement))
                {
                    yield return nested;
                }
                yield break;
            }

            if (element is ScrollViewer scrollViewer)
            {
                foreach (var nested in EnumeratePermissionCheckBoxes(scrollViewer.Content as UIElement))
                {
                    yield return nested;
                }
                yield break;
            }

            if (element is ContentControl contentControl && contentControl.Content is UIElement content)
            {
                foreach (var nested in EnumeratePermissionCheckBoxes(content))
                {
                    yield return nested;
                }
            }
        }

        private Grid CreateAuditLogHeaderRow()
        {
            var grid = CreateAuditGrid();
            grid.Padding = new Thickness(12, 8, 12, 8);
            grid.Background = ThemeBrush("BackgroundSecondaryBrush");

            grid.Children.Add(CreateAuditHeaderText("Thời gian", 0));
            grid.Children.Add(CreateAuditHeaderText("Người thực hiện", 1));
            grid.Children.Add(CreateAuditHeaderText("Thao tác", 2));
            grid.Children.Add(CreateAuditHeaderText("Đối tượng", 3));
            grid.Children.Add(CreateAuditHeaderText("Chi tiết", 4));

            return grid;
        }

        private UIElement CreateAuditLogRow(AuditLogDisplayItem entry)
        {
            var container = new StackPanel
            {
                Spacing = 0
            };

            var grid = CreateAuditGrid();
            grid.Padding = new Thickness(12, 10, 12, 10);

            grid.Children.Add(CreateAuditCellText(entry.TimestampText, 0, TextAlignment.Center));
            grid.Children.Add(CreateAuditCellText(entry.ActorDisplayName, 1, TextAlignment.Center));
            grid.Children.Add(CreateAuditCellText(entry.ActionDisplayName, 2, TextAlignment.Center));
            grid.Children.Add(CreateAuditCellText(entry.TargetDisplayName, 3, TextAlignment.Center));
            grid.Children.Add(CreateAuditCellText(entry.Summary, 4, TextAlignment.Left));

            container.Children.Add(grid);
            container.Children.Add(new Border
            {
                Height = 1,
                Background = ThemeBrush("BorderLightBrush"),
                Opacity = 1
            });

            return container;
        }

        private Grid CreateAuditGrid()
        {
            var grid = new Grid
            {
                ColumnSpacing = 12
            };

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.5, GridUnitType.Star) });
            return grid;
        }

        private TextBlock CreateAuditHeaderText(string text, int column)
        {
            var textBlock = new TextBlock
            {
                Text = text,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeBrush("TextSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(textBlock, column);
            return textBlock;
        }

        private TextBlock CreateAuditCellText(string text, int column, TextAlignment alignment)
        {
            var textBlock = new TextBlock
            {
                Text = text,
                Foreground = ThemeBrush("TextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = alignment == TextAlignment.Left
                    ? HorizontalAlignment.Stretch
                    : HorizontalAlignment.Center,
                TextAlignment = alignment,
                TextWrapping = TextWrapping.WrapWholeWords
            };
            Grid.SetColumn(textBlock, column);
            return textBlock;
        }

        private static IEnumerable<AuditLogDisplayItem> FilterAuditLogs(
            IReadOnlyCollection<AuditLogDisplayItem> entries,
            string? searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
            {
                return entries;
            }

            return entries.Where(entry =>
                ContainsIgnoreCase(entry.ActorDisplayName, searchText)
                || ContainsIgnoreCase(entry.ActionDisplayName, searchText)
                || ContainsIgnoreCase(entry.TargetDisplayName, searchText)
                || ContainsIgnoreCase(entry.Summary, searchText));
        }

        private static bool ContainsIgnoreCase(string? value, string searchText)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.Contains(searchText, StringComparison.OrdinalIgnoreCase);
        }

        private static AuditLogDisplayItem MapAuditLog(AuditLogDto log)
        {
            var summarySource = !string.IsNullOrWhiteSpace(log.NewValueJson)
                ? log.NewValueJson
                : log.OldValueJson;

            return new AuditLogDisplayItem(
                log.Id,
                string.IsNullOrWhiteSpace(log.ActorDisplayName) ? "Hệ thống" : log.ActorDisplayName,
                GetAuditActionDisplayName(log.Action),
                GetAuditTargetDisplayName(log.TargetType),
                BuildAuditSummary(summarySource, log.TargetId),
                log.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"));
        }

        private static string GetAuditActionDisplayName(string action)
        {
            return action?.Trim().ToUpperInvariant() switch
            {
                "USER_CREATED" => "Tạo người dùng",
                "USER_UPDATED" => "Cập nhật người dùng",
                "USER_DELETED" => "Xóa người dùng",
                "USER_ACCESS_UPDATED" => "Cập nhật quyền người dùng",
                "ROLE_CREATED" => "Tạo vai trò",
                "ROLE_UPDATED" => "Cập nhật vai trò",
                "ROLE_DELETED" => "Xóa vai trò",
                "LOGIN_FAILED" => "Đăng nhập thất bại",
                "ACCOUNT_TEMPORARILY_LOCKED" => "Khoá tạm thời tài khoản",
                "PASSWORD_CHANGED" => "Đổi mật khẩu",
                "PROFILE_UPDATED" => "Cập nhật hồ sơ",
                _ => action
            };
        }

        private static string GetAuditTargetDisplayName(string targetType)
        {
            return targetType?.Trim().ToUpperInvariant() switch
            {
                "USER" => "Người dùng",
                "ROLE" => "Vai trò",
                _ => string.IsNullOrWhiteSpace(targetType) ? "Khác" : targetType
            };
        }

        private static string BuildAuditSummary(string? json, string fallbackTargetId)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return fallbackTargetId;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                var parts = new List<string>();
                AddJsonValue(parts, root, "Username");
                AddJsonValue(parts, root, "FullName");
                AddJsonValue(parts, root, "Code");
                AddJsonValue(parts, root, "Name");

                if (parts.Count > 0)
                {
                    return string.Join(" | ", parts.Distinct(StringComparer.OrdinalIgnoreCase));
                }
            }
            catch
            {
                // Ignore parse issues and fall back to raw summary.
            }

            return fallbackTargetId;
        }

        private static void AddJsonValue(List<string> values, JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var property))
            {
                return;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                var value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }
            }
        }

        private sealed record PermissionGroupDefinition(string Name, IReadOnlyList<PermissionActionDefinition> Actions);

        private sealed record PermissionActionDefinition(string DisplayName, string PermissionCode);

        private sealed record AuditLogDisplayItem(
            Guid Id,
            string ActorDisplayName,
            string ActionDisplayName,
            string TargetDisplayName,
            string Summary,
            string TimestampText);

        private SolidColorBrush DialogBlueBrush()
        {
            if (IsDarkThemeActive())
            {
                return new SolidColorBrush(Color.FromArgb(255, 11, 23, 40));
            }

            return ThemeBrush("BackgroundSecondaryBrush");
        }

        private SolidColorBrush SurfaceBlueBrush()
        {
            if (IsDarkThemeActive())
            {
                return new SolidColorBrush(Color.FromArgb(255, 11, 23, 40));
            }

            return ThemeBrush("BackgroundSecondaryBrush");
        }

        private SolidColorBrush TransparentBrush()
        {
            return new SolidColorBrush(Colors.Transparent);
        }

        private SolidColorBrush BorderSoftBrush()
        {
            if (IsDarkThemeActive())
            {
                return new SolidColorBrush(Color.FromArgb(255, 26, 49, 77));
            }

            return ThemeBrush("BorderLightBrush");
        }

        private SolidColorBrush InputBorderBrush()
        {
            return ThemeBrush("TextControlBorderBrush");
        }

        private SolidColorBrush DialogInputBackgroundBrush()
        {
            if (IsDarkThemeActive())
            {
                return new SolidColorBrush(Color.FromArgb(255, 23, 30, 51));
            }

            return ThemeBrush("BackgroundSecondaryBrush");
        }

        private SolidColorBrush DialogInputBorderBrush()
        {
            if (IsDarkThemeActive())
            {
                return new SolidColorBrush(Color.FromArgb(255, 45, 50, 56));
            }

            return ThemeBrush("TextControlBorderBrush");
        }

        private SolidColorBrush DialogInputBorderFocusedBrush()
        {
            if (IsDarkThemeActive())
            {
                return new SolidColorBrush(Color.FromArgb(255, 41, 121, 255));
            }

            return ThemeBrush("AccentBrush");
        }

        private SolidColorBrush DialogInputForegroundBrush()
        {
            if (IsDarkThemeActive())
            {
                return new SolidColorBrush(Color.FromArgb(255, 226, 232, 240));
            }

            return ThemeBrush("TextPrimaryBrush");
        }

        private SolidColorBrush DialogInputPlaceholderBrush()
        {
            if (IsDarkThemeActive())
            {
                return new SolidColorBrush(Color.FromArgb(255, 148, 163, 184));
            }

            return ThemeBrush("TextSecondaryBrush");
        }

        private SolidColorBrush DialogInputHeaderBrush()
        {
            if (IsDarkThemeActive())
            {
                return new SolidColorBrush(Color.FromArgb(255, 241, 245, 249));
            }

            return ThemeBrush("TextPrimaryBrush");
        }

        private bool IsDarkThemeActive()
        {
            return ThemeService.Instance.CurrentTheme != ElementTheme.Light;
        }

        private SolidColorBrush BorderStrongBrush()
        {
            return ThemeBrush("BorderPrimaryBrush");
        }

        private SolidColorBrush WhiteTextBrush()
        {
            return ThemeBrush("TextPrimaryBrush");
        }

        private SolidColorBrush DarkTextBrush()
        {
            return ThemeBrush("TextPrimaryBrush");
        }

        private SolidColorBrush MutedTextBrush()
        {
            return ThemeBrush("TextSecondaryBrush");
        }

        private Brush BrushResource(string key)
        {
            return (Brush)Station.Services.ThemeBrushes.Get(key);
        }

        private SolidColorBrush ThemeBrush(string key)
        {
            return (SolidColorBrush)Station.Services.ThemeBrushes.Get(key);
        }

        private Style AppStyle(string key)
        {
            return (Style)Station.Services.ThemeBrushes.Get(key);
        }

        private Style? TryStyle(string key)
        {
            return Station.Services.ThemeBrushes.TryGet(key, out var value)
                ? value as Style
                : null;
        }

        private Style CreatePrimaryDialogButtonStyle()
        {
            var style = new Style(typeof(Button));
            var primary = new SolidColorBrush(Color.FromArgb(255, 37, 99, 235));
            var foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));

            style.Setters.Add(new Setter(Control.BackgroundProperty, primary));
            style.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, ThemeBrush("BorderLightBrush")));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(10)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(20, 0, 20, 0)));
            style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 44d));
            style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 44d));
            style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0d));
            style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, double.PositiveInfinity));
            style.Setters.Add(new Setter(Control.MinHeightProperty, 44d));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));

            return style;
        }

        private Style CreateCancelDialogButtonStyle()
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, ThemeBrush("BackgroundSecondaryBrush")));
            style.Setters.Add(new Setter(Control.ForegroundProperty, ThemeBrush("TextPrimaryBrush")));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, ThemeBrush("BorderLightBrush")));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(10)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(20, 0, 20, 0)));
            style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 44d));
            style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 44d));
            style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0d));
            style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, double.PositiveInfinity));
            style.Setters.Add(new Setter(Control.MinHeightProperty, 44d));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
            return style;
        }

        private void NormalizeDialogActionButtons(ContentDialog dialog)
        {
            var buttons = new List<Button>();

            foreach (var buttonName in new[] { "PrimaryButton", "SecondaryButton", "CloseButton" })
            {
                if (FindNamedDescendant<Button>(dialog, buttonName) is not Button button)
                {
                    continue;
                }

                buttons.Add(button);
                button.Height = 44;
                button.MinHeight = 44;
                button.MaxHeight = 44;
                button.Padding = new Thickness(20, 0, 20, 0);
                button.Margin = new Thickness(0);
                button.VerticalAlignment = VerticalAlignment.Center;
                button.VerticalContentAlignment = VerticalAlignment.Center;
                button.HorizontalContentAlignment = HorizontalAlignment.Center;
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
            }

            var actionButtons = buttons
                .Where(button => string.Equals(button.Name, "PrimaryButton", StringComparison.Ordinal)
                    || string.Equals(button.Name, "SecondaryButton", StringComparison.Ordinal)
                    || string.Equals(button.Name, "CloseButton", StringComparison.Ordinal))
                .ToList();

            if (actionButtons.Count < 2)
            {
                return;
            }

            var sharedParent = VisualTreeHelper.GetParent(actionButtons[0]);
            if (sharedParent is Grid parentGrid
                && actionButtons.All(button => ReferenceEquals(VisualTreeHelper.GetParent(button), parentGrid)))
            {
                var usedColumns = actionButtons
                    .Select(Grid.GetColumn)
                    .Distinct()
                    .Where(index => index >= 0 && index < parentGrid.ColumnDefinitions.Count)
                    .ToList();

                foreach (var columnIndex in usedColumns)
                {
                    parentGrid.ColumnDefinitions[columnIndex].Width = new GridLength(1, GridUnitType.Star);
                }
            }

            var buttonHosts = actionButtons
                .Select(GetButtonLayoutHost)
                .Where(host => host != null)
                .Cast<FrameworkElement>()
                .Distinct()
                .ToList();

            var uniformWidth = buttonHosts.Count >= 2
                ? buttonHosts.Max(host => Math.Max(host.ActualWidth, host.MinWidth))
                : actionButtons.Max(button => Math.Max(button.ActualWidth, button.MinWidth));

            if (uniformWidth <= 0)
            {
                uniformWidth = 180;
            }

            foreach (var host in buttonHosts)
            {
                host.Width = uniformWidth;
                host.MinWidth = uniformWidth;
                host.MaxWidth = uniformWidth;
                host.HorizontalAlignment = HorizontalAlignment.Stretch;
                host.Margin = new Thickness(0);
            }

            foreach (var button in actionButtons)
            {
                button.Width = double.NaN;
                button.MinWidth = 0;
                button.MaxWidth = double.PositiveInfinity;
            }
        }

        private static FrameworkElement? GetButtonLayoutHost(Button button)
        {
            var current = VisualTreeHelper.GetParent(button);
            while (current is FrameworkElement element)
            {
                if (current is Grid || current is Border || current is ContentPresenter)
                {
                    return element;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private static T? FindNamedDescendant<T>(DependencyObject root, string name)
            where T : FrameworkElement
        {
            if (root is T typed && string.Equals(typed.Name, name, StringComparison.Ordinal))
            {
                return typed;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < childCount; i++)
            {
                var match = FindNamedDescendant<T>(VisualTreeHelper.GetChild(root, i), name);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }
    }
}
