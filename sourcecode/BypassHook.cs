using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

public class StartupHook
{
    private static readonly int[] TargetTokens =
    {
        0x06000796,
        0x06000797,
        0x06000798,
        0x06000799,
        0x0600079D,
    };

    private static readonly int[] VoidNoopTokens =
    {
        0x0600004B,
        0x060000BD,
    };

    private static readonly int[] ClientRefreshTokens =
    {
        0x060006AA,
        0x060006AB,
        0x060006A8,
        0x060006A9,
    };

    private static int _assemblyPatched;
    private static int _uiBypassed;
    private static int _attachBindingsLogged;
    private static int _attachRetryTicks;
    private static int _clientsTabInvoked;
    private static int _clientsRefreshInvoked;
    private static int _selectToggleInvoked;
    private static int _clientInjected;
    private static int _clientWarmupAttempts;
    private static DispatcherTimer _timer;
    private static readonly Dictionary<int, IntPtr> AttachedHandles = new Dictionary<int, IntPtr>();
    private static readonly bool EnableVoidNoops =
        !string.Equals(Environment.GetEnvironmentVariable("WAVE_PATCH_ENABLE_VOID_NOOPS"), "0", StringComparison.OrdinalIgnoreCase);

    public static void Initialize()
    {
        try
        {
            Log("[init] startup hook loaded");
            Log("[init] void-noops=" + (EnableVoidNoops ? "enabled" : "disabled"));
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                TryPatchAssembly(asm);

            StartUiTimer();
        }
        catch (Exception ex)
        {
            Log("[init-error] " + ex);
        }
    }

    private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs e)
    {
        TryPatchAssembly(e.LoadedAssembly);
        StartUiTimer();
    }

    private static void StartUiTimer()
    {
        try
        {
            var app = Application.Current;
            if (app == null)
                return;

            app.Dispatcher.InvokeAsync(() =>
            {
                if (_timer != null)
                    return;

                _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, app.Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(750)
                };
                _timer.Tick += delegate(object sender, EventArgs e) { TryBypassUi(); };
                _timer.Start();
                Log("[ui] timer-started");
            });
        }
        catch (Exception ex)
        {
            Log("[ui-timer-error] " + ex);
        }
    }

    private static void TryPatchAssembly(Assembly asm)
    {
        var name = asm.GetName().Name ?? "";
        if (!name.Equals("Wave", StringComparison.OrdinalIgnoreCase))
            return;

        if (System.Threading.Interlocked.Exchange(ref _assemblyPatched, 1) != 0)
            return;

        int count = 0;
        foreach (var type in SafeGetTypes(asm))
        {
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            }
            catch
            {
                continue;
            }

            foreach (var method in methods)
            {
                if (method.IsAbstract || method.ContainsGenericParameters || method.IsSpecialName)
                    continue;

                try
                {
                    if (EnableVoidNoops && VoidNoopTokens.Contains(method.MetadataToken) && method.ReturnType == typeof(void))
                    {
                        PatchMethod(method, typeof(StartupHook).GetMethod("ReturnVoid", BindingFlags.NonPublic | BindingFlags.Static));
                        Log("[patch] void " + Describe(method));
                        count++;
                        continue;
                    }

                    if (!TargetTokens.Contains(method.MetadataToken))
                        continue;

                    if (method.ReturnType == typeof(bool))
                    {
                        PatchMethod(method, typeof(StartupHook).GetMethod("ReturnTrue", BindingFlags.NonPublic | BindingFlags.Static));
                        Log("[patch] bool " + Describe(method));
                        count++;
                    }
                    else if (method.ReturnType.IsGenericType &&
                             method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>) &&
                             method.ReturnType.GetGenericArguments()[0] == typeof(bool))
                    {
                        PatchMethod(method, typeof(StartupHook).GetMethod("ReturnTrueTask", BindingFlags.NonPublic | BindingFlags.Static));
                        Log("[patch] task<bool> " + Describe(method));
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    Log("[patch-error] " + Describe(method) + " :: " + ex.GetType().Name + " " + ex.Message);
                }
            }
        }

        Log("[patch] total=" + count);
    }

    private static void TryBypassUi()
    {
        try
        {
            var app = Application.Current;
            if (app == null || app.Windows.Count == 0)
                return;

            foreach (Window window in app.Windows)
            {
                if (window == null || !window.IsLoaded)
                    continue;

                var uiAlreadyBypassed = Interlocked.CompareExchange(ref _uiBypassed, 0, 0) == 1;
                if (!uiAlreadyBypassed)
                    LogWindow(window);

                var nodes = EnumerateVisuals(window).ToList();
                var keyPanel = nodes.OfType<FrameworkElement>().FirstOrDefault(x => IsNamed(x, "KeyPanel"));
                var verifyPanel = nodes.OfType<FrameworkElement>().FirstOrDefault(x => IsNamed(x, "Verify"));
                var loaderPage = nodes.OfType<FrameworkElement>().FirstOrDefault(x => IsNamed(x, "LoaderPage"));
                var loader = nodes.OfType<FrameworkElement>().FirstOrDefault(x => IsNamed(x, "Loader"));
                var loaderProgress = nodes.OfType<FrameworkElement>().FirstOrDefault(x => IsNamed(x, "LoaderProgress"));
                var progressBar = nodes.OfType<FrameworkElement>().FirstOrDefault(x => IsNamed(x, "ProgressBar"));
                var keyBox = nodes.OfType<FrameworkElement>().FirstOrDefault(x => IsNamed(x, "Key"));
                var loginButton = nodes.OfType<FrameworkElement>().FirstOrDefault(x => IsNamed(x, "Login"));
                var getKeyButton = nodes.OfType<FrameworkElement>().FirstOrDefault(x => IsNamed(x, "GetKey"));
                var homePage = nodes.OfType<FrameworkElement>().FirstOrDefault(x => IsNamed(x, "Homepage"));
                var titlebar = nodes.OfType<FrameworkElement>().FirstOrDefault(x => IsNamed(x, "Titlebar"));
                var controls = nodes.OfType<FrameworkElement>().FirstOrDefault(x => IsNamed(x, "Controls"));
                var minimize = nodes.OfType<FrameworkElement>().FirstOrDefault(x => IsNamed(x, "MinimizeT"));
                var maximize = nodes.OfType<FrameworkElement>().FirstOrDefault(x => IsNamed(x, "MaximizeT"));
                var close = nodes.OfType<FrameworkElement>().FirstOrDefault(x => IsNamed(x, "CloseT"));
                var minimizeKey = nodes.OfType<FrameworkElement>().FirstOrDefault(x => IsNamed(x, "MinimizeKT"));
                var closeKey = nodes.OfType<FrameworkElement>().FirstOrDefault(x => IsNamed(x, "CloseKT"));

                if (keyBox == null || loginButton == null)
                    continue;

                if (!uiAlreadyBypassed)
                {
                    var tb = keyBox as TextBox;
                    if (tb != null && string.IsNullOrWhiteSpace(tb.Text))
                        tb.Text = "patched";

                    var overlay = keyPanel != null ? (DependencyObject)keyPanel : FindOverlay(window, keyBox, loginButton, getKeyButton);
                    var overlayElement = overlay as UIElement;
                    if (overlayElement != null)
                    {
                        overlayElement.Visibility = Visibility.Collapsed;
                        overlayElement.IsEnabled = false;
                        Log("[ui] collapsed " + DescribeElement(overlayElement));
                    }

                    if (verifyPanel != null)
                    {
                        var verifyUi = verifyPanel as UIElement;
                        if (verifyUi != null)
                        {
                            verifyUi.Visibility = Visibility.Collapsed;
                            verifyUi.IsEnabled = false;
                            Log("[ui] collapsed " + DescribeElement(verifyUi));
                        }
                    }

                    CollapseElement(loaderPage);
                    CollapseElement(loader);
                    CollapseElement(loaderProgress);
                    CollapseElement(progressBar);
                    ShowElement(titlebar);
                    ShowElement(controls);
                    ShowElement(minimize);
                    ShowElement(maximize);
                    ShowElement(close);
                    ShowElement(minimizeKey);
                    ShowElement(closeKey);
                    ForceHeaderButtonVisuals(minimize);
                    ForceHeaderButtonVisuals(maximize);
                    ForceHeaderButtonVisuals(close);
                    ForceHeaderButtonVisuals(minimizeKey);
                    ForceHeaderButtonVisuals(closeKey);

                    if (homePage != null)
                    {
                        var homeUi = homePage as UIElement;
                        if (homeUi != null)
                        {
                            homeUi.Visibility = Visibility.Visible;
                            homeUi.IsEnabled = true;
                            Log("[ui] showed " + DescribeElement(homeUi));
                        }
                    }

                    foreach (var element in new FrameworkElement[] { keyBox, loginButton, getKeyButton })
                    {
                        var ui = element as UIElement;
                        if (ui != null)
                        {
                            ui.Visibility = Visibility.Collapsed;
                            ui.IsEnabled = false;
                        }
                    }
                }

                if (Interlocked.Exchange(ref _attachBindingsLogged, 1) == 0)
                    LogAttachBindings(nodes, window.DataContext);

                var attachReady = TrySelectClientsViaRealAttach(nodes, window.DataContext);
                var ticks = Interlocked.Increment(ref _attachRetryTicks);

                if (!uiAlreadyBypassed)
                {
                    Interlocked.Exchange(ref _uiBypassed, 1);
                    Log("[ui] bypass-applied");
                }

                if ((attachReady || ticks >= 40) && _timer != null)
                    _timer.Stop();

                return;
            }
        }
        catch (Exception ex)
        {
            Log("[ui-error] " + ex);
        }
    }

    private static DependencyObject FindOverlay(Window window, FrameworkElement keyBox, FrameworkElement loginButton, FrameworkElement getKeyButton)
    {
        var ancestor = LowestCommonAncestor(keyBox, loginButton);
        if (getKeyButton != null)
            ancestor = LowestCommonAncestor(ancestor, getKeyButton);

        while (ancestor != null && ancestor != window)
        {
            if (ancestor is Grid || ancestor is Border || ancestor is StackPanel || ancestor is DockPanel || ancestor is ContentControl)
                return ancestor;
            ancestor = VisualTreeHelper.GetParent(ancestor);
        }

        return LowestAncestorUnderWindow(keyBox, window);
    }

    private static DependencyObject LowestCommonAncestor(DependencyObject a, DependencyObject b)
    {
        if (a == null || b == null)
            return a ?? b;

        var set = new HashSet<DependencyObject>();
        for (var cur = a; cur != null; cur = VisualTreeHelper.GetParent(cur))
            set.Add(cur);

        for (var cur = b; cur != null; cur = VisualTreeHelper.GetParent(cur))
        {
            if (set.Contains(cur))
                return cur;
        }

        return null;
    }

    private static DependencyObject LowestAncestorUnderWindow(DependencyObject child, Window window)
    {
        DependencyObject last = child;
        for (var cur = child; cur != null && cur != window; cur = VisualTreeHelper.GetParent(cur))
            last = cur;
        return last;
    }

    private static IEnumerable<DependencyObject> EnumerateVisuals(DependencyObject root)
    {
        yield return root;
        int count;
        try { count = VisualTreeHelper.GetChildrenCount(root); }
        catch { yield break; }

        for (int i = 0; i < count; i++)
        {
            DependencyObject child;
            try { child = VisualTreeHelper.GetChild(root, i); }
            catch { continue; }
            if (child == null)
                continue;

            foreach (var nested in EnumerateVisuals(child))
                yield return nested;
        }
    }

    private static void LogAttachBindings(List<DependencyObject> nodes, object dataContext)
    {
        try
        {
            var buttons = nodes.OfType<ButtonBase>().ToList();
            var selectToggle = buttons.FirstOrDefault(b => IsNamed(b, "SelectToggleBtn"));
            var execute = buttons.FirstOrDefault(b => IsNamed(b, "Execute"));
            var clientsTab = nodes.OfType<FrameworkElement>().FirstOrDefault(x => IsNamed(x, "ClientsT")) as ToggleButton;

            if (clientsTab != null)
            {
                Log("[attach-ui] ClientsT checked=" + ((clientsTab.IsChecked ?? false) ? "true" : "false"));
                LogClickHandlers("ClientsT", clientsTab);
            }

            LogButtonBinding("SelectToggleBtn", selectToggle);
            LogButtonBinding("Execute", execute);

            if (dataContext != null)
            {
                var type = dataContext.GetType();
                Log("[attach-ctx] " + type.FullName);

                var clientItemType = ResolveClientItemType(dataContext);
                if (clientItemType != null)
                    Log("[attach-ctx] client-item-type=" + clientItemType.FullName);

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (method.IsSpecialName)
                        continue;

                    var ps = method.GetParameters();
                    bool maybeRelevant = false;
                    if (clientItemType != null && ps.Any(p => p.ParameterType == clientItemType))
                        maybeRelevant = true;
                    if (method.ReturnType == typeof(Task) && ps.Any(p => p.ParameterType == typeof(bool)))
                        maybeRelevant = true;
                    if (ps.Any(p => p.ParameterType == typeof(bool)) && method.ReturnType == typeof(void))
                        maybeRelevant = true;

                    if (!maybeRelevant)
                        continue;

                    Log("[attach-candidate] " + Describe(method) + " sig=(" + string.Join(",", ps.Select(p => p.ParameterType.Name).ToArray()) + ")");
                }
            }
        }
        catch (Exception ex)
        {
            Log("[attach-bindings-error] " + ex.GetType().Name + " " + ex.Message);
        }
    }

    private static void LogButtonBinding(string label, ButtonBase button)
    {
        if (button == null)
        {
            Log("[attach-ui] " + label + " not-found");
            return;
        }

        var command = button.Command;
        var parameter = button.CommandParameter;
        var cmdType = command != null ? command.GetType().FullName : "<null>";
        var parameterType = parameter != null ? parameter.GetType().FullName : "<null>";
        Log("[attach-ui] " + label + " command=" + cmdType + " paramType=" + parameterType);

        if (command == null)
        {
            LogClickHandlers(label, button);
            return;
        }

        foreach (var field in command.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            object value;
            try { value = field.GetValue(command); }
            catch { continue; }

            if (value is Delegate d)
            {
                foreach (var del in d.GetInvocationList())
                {
                    var m = del.Method;
                    var token = m != null ? m.MetadataToken.ToString("X8") : "00000000";
                    var dt = m != null && m.DeclaringType != null ? m.DeclaringType.FullName : "<null>";
                    var mn = m != null ? m.Name : "<null>";
                    Log("[attach-ui] " + label + " delegate " + token + " " + dt + "::" + mn);
                }
            }
        }

        LogClickHandlers(label, button);
    }

    private static void LogClickHandlers(string label, ButtonBase button)
    {
        try
        {
            var uiElementType = typeof(UIElement);
            var storeProp = uiElementType.GetProperty("EventHandlersStore", BindingFlags.Instance | BindingFlags.NonPublic);
            if (storeProp == null)
            {
                Log("[attach-ui] " + label + " click-handlers store-missing");
                return;
            }

            var store = storeProp.GetValue(button, null);
            if (store == null)
            {
                Log("[attach-ui] " + label + " click-handlers none");
                return;
            }

            var getHandlers = store.GetType().GetMethod("GetRoutedEventHandlers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (getHandlers == null)
            {
                Log("[attach-ui] " + label + " click-handlers api-missing");
                return;
            }

            var infos = getHandlers.Invoke(store, new object[] { ButtonBase.ClickEvent }) as Array;
            if (infos == null || infos.Length == 0)
            {
                Log("[attach-ui] " + label + " click-handlers empty");
                return;
            }

            foreach (var info in infos)
            {
                if (info == null)
                    continue;

                var handlerProp = info.GetType().GetProperty("Handler", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var handler = handlerProp != null ? handlerProp.GetValue(info, null) as Delegate : null;
                if (handler == null)
                    continue;

                var method = handler.Method;
                var token = method != null ? method.MetadataToken.ToString("X8") : "00000000";
                var dt = method != null && method.DeclaringType != null ? method.DeclaringType.FullName : "<null>";
                var mn = method != null ? method.Name : "<null>";
                Log("[attach-ui] " + label + " click " + token + " " + dt + "::" + mn);
            }
        }
        catch (Exception ex)
        {
            Log("[attach-ui] " + label + " click-handlers-error " + ex.GetType().Name);
        }
    }

    private static Type ResolveClientItemType(object dataContext)
    {
        try
        {
            var type = dataContext.GetType();
            var prop = type.GetProperty("ClientItems", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop == null || !prop.CanRead)
                return null;

            var collection = prop.GetValue(dataContext, null);
            if (collection == null)
                return null;

            var collectionType = collection.GetType();
            if (collectionType.IsGenericType)
                return collectionType.GetGenericArguments()[0];
        }
        catch
        {
        }

        return null;
    }

    private static bool TrySelectClientsViaRealAttach(List<DependencyObject> nodes, object dataContext)
    {
        if (dataContext == null)
            return false;

        try
        {
            var type = dataContext.GetType();
            var prop = type.GetProperty("ClientItems", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop == null || !prop.CanRead)
                return false;

            var collection = prop.GetValue(dataContext, null);
            if (collection == null)
                return false;

            var count = TryGetCount(collection);
            int selected = 0;
            foreach (var item in EnumerateCollectionItems(collection))
            {
                if (ReadBoolMember(item, "IsSelected") == true)
                    selected++;
            }

            Log("[attach] clients=" + count + " selected=" + selected);
            if (count <= 0 || selected > 0)
            {
                if (count <= 0)
                {
                    var clientsTab = nodes.OfType<ToggleButton>().FirstOrDefault(x => IsNamed(x, "ClientsT"));
                    if (clientsTab != null && Interlocked.Exchange(ref _clientsTabInvoked, 1) == 0)
                    {
                        clientsTab.IsChecked = true;
                        clientsTab.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, clientsTab));
                        clientsTab.RaiseEvent(new RoutedEventArgs(ToggleButton.CheckedEvent, clientsTab));
                        Log("[attach] invoked ClientsT real handler");
                    }

                    if (Interlocked.Exchange(ref _clientsRefreshInvoked, 1) == 0)
                    {
                        TryInvokeMethodByToken(dataContext, 0x060006A7, true);
                        TryInvokeMethodByToken(dataContext, 0x060006AB);
                    }
                }

                return selected > 0;
            }

            var toggle = nodes.OfType<ButtonBase>().FirstOrDefault(b => IsNamed(b, "SelectToggleBtn"));
            if (toggle == null)
                return false;

            if (Interlocked.Exchange(ref _selectToggleInvoked, 1) == 0)
            {
                toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, toggle));
                Log("[attach] invoked SelectToggleBtn real handler");
            }
        }
        catch (Exception ex)
        {
            Log("[attach-error] " + ex.GetType().Name + " " + ex.Message);
        }

        return false;
    }

    private static void TryInvokeMethodByToken(object target, int token, params object[] args)
    {
        if (target == null)
            return;

        try
        {
            var method = target.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.MetadataToken == token);
            if (method == null)
            {
                Log("[attach] token-missing " + token.ToString("X8"));
                return;
            }

            var parameters = method.GetParameters();
            var callArgs = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                if (args != null && i < args.Length)
                    callArgs[i] = args[i];
                else
                    callArgs[i] = GetDefaultValue(parameters[i].ParameterType);
            }

            var result = method.Invoke(target, callArgs);
            var task = result as Task;
            if (task != null)
            {
                task.ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                        Log("[attach-token-error] " + token.ToString("X8") + " :: " + t.Exception.GetBaseException().Message);
                });
            }
            Log("[attach] invoked token " + token.ToString("X8"));
        }
        catch (Exception ex)
        {
            Log("[attach-token-error] " + token.ToString("X8") + " :: " + ex.GetType().Name);
        }
    }

    private static object GetDefaultValue(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(bool))
            return false;
        if (t == typeof(string))
            return string.Empty;
        if (t == typeof(int))
            return 0;
        if (t == typeof(uint))
            return 0u;
        if (t == typeof(long))
            return 0L;
        if (t == typeof(ulong))
            return 0UL;
        if (t == typeof(IntPtr))
            return IntPtr.Zero;
        if (t == typeof(CancellationToken))
            return CancellationToken.None;
        if (t.IsArray)
            return Array.CreateInstance(t.GetElementType() ?? typeof(object), 0);
        if (t.IsValueType)
            return Activator.CreateInstance(t);
        return null;
    }

    private static void ForceLikelySuccessState(object dataContext)
    {
        if (dataContext == null)
            return;

        try
        {
            var type = dataContext.GetType();
            Log("[ctx] " + type.FullName);

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!prop.CanRead)
                    continue;

                if (prop.GetIndexParameters().Length != 0)
                    continue;

                object value = null;
                try { value = prop.GetValue(dataContext); } catch { }
                Log("[ctx-prop] " + prop.Name + "=" + FormatValue(value));

                if (!prop.CanWrite)
                    continue;

                try
                {
                    if (prop.PropertyType == typeof(bool) && LooksPositive(prop.Name))
                        prop.SetValue(dataContext, true);
                    else if (prop.PropertyType == typeof(bool?) && LooksPositive(prop.Name))
                        prop.SetValue(dataContext, true);
                    else if (prop.PropertyType == typeof(string) && prop.Name.IndexOf("Key", StringComparison.OrdinalIgnoreCase) >= 0)
                        prop.SetValue(dataContext, "patched");
                    else if (prop.PropertyType == typeof(string) && prop.Name.IndexOf("Hwid", StringComparison.OrdinalIgnoreCase) >= 0)
                        prop.SetValue(dataContext, "patched-hwid");
                    else if (prop.PropertyType == typeof(string) && prop.Name.IndexOf("User", StringComparison.OrdinalIgnoreCase) >= 0)
                        prop.SetValue(dataContext, "alvaro");
                    else if (prop.PropertyType == typeof(int) &&
                             (prop.Name.IndexOf("Selected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              prop.Name.IndexOf("Index", StringComparison.OrdinalIgnoreCase) >= 0))
                        prop.SetValue(dataContext, 0);
                }
                catch (Exception ex)
                {
                    Log("[ctx-set-error] " + prop.Name + " :: " + ex.GetType().Name);
                }
            }
        }
        catch (Exception ex)
        {
            Log("[ctx-error] " + ex);
        }
    }

    private static bool LooksPositive(string name)
    {
        return name.IndexOf("claim", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("valid", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("active", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("login", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("license", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsNamed(FrameworkElement element, string name)
    {
        return string.Equals(element.Name, name, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(AutomationProperties.GetAutomationId(element), name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EnsureRobloxClient(object dataContext)
    {
        if (dataContext == null)
            return false;

        try
        {
            var type = dataContext.GetType();
            var prop = type.GetProperty("ClientItems", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop == null || !prop.CanRead)
            {
                Log("[clients] ClientItems property missing");
                return false;
            }

            var collection = prop.GetValue(dataContext, null);
            if (collection == null)
            {
                Log("[clients] ClientItems collection is null");
                return false;
            }

            var processes = FindProcessesByName("RobloxPlayerBeta");
            CleanupDeadHandles(processes);

            var count = TryGetCount(collection);
            if (count <= 0)
            {
                var attempt = Interlocked.Increment(ref _clientWarmupAttempts);
                if (attempt <= 3)
                {
                    var sampleItem = EnumerateCollectionItems(collection).FirstOrDefault();
                    TryInvokeClientDiscovery(dataContext, processes.FirstOrDefault(), sampleItem);
                    Log("[clients] warmup attempt=" + attempt);
                    return false;
                }
            }

            if (count <= 0)
            {
                if (processes == null || processes.Length == 0)
                {
                    Log("[clients] RobloxPlayerBeta.exe not running");
                    return false;
                }

                if (Interlocked.Exchange(ref _clientInjected, 1) == 0)
                {
                    var collectionType = collection.GetType();
                    Type itemType = null;
                    if (collectionType.IsGenericType)
                        itemType = collectionType.GetGenericArguments()[0];
                    if (itemType == null)
                    {
                        Log("[clients] cannot resolve client item type");
                        return false;
                    }

                    var process = processes[0];
                    var item = CreateClientItem(itemType, process);
                    if (item == null)
                    {
                        Log("[clients] cannot create client item");
                        return false;
                    }

                    var list = collection as IList;
                    if (list != null)
                    {
                        list.Add(item);
                    }
                    else
                    {
                        var add = collectionType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
                        if (add == null)
                        {
                            Log("[clients] Add method missing");
                            return false;
                        }
                        add.Invoke(collection, new object[] { item });
                    }

                    Log("[clients] injected RobloxPlayerBeta.exe into ClientItems");
                }
            }

            var attached = SyncClientAttachmentState(collection, processes);
            var finalCount = TryGetCount(collection);
            Log("[clients] count=" + finalCount + " attached=" + attached);
            return attached > 0;
        }
        catch (Exception ex)
        {
            Log("[clients-error] " + ex);
            return false;
        }
    }

    private static object CreateClientItem(Type itemType, Process process)
    {
        var ctors = itemType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (var ctor in ctors
            .OrderByDescending(ScoreCtor)
            .ThenByDescending(c => c.GetParameters().Length))
        {
            var args = BuildCtorArgs(ctor, process);
            if (args == null)
                continue;

            try
            {
                var item = ctor.Invoke(args);
                Log("[clients] ctor " + Describe(ctor) + " args=" + args.Length);
                IntPtr handle;
                var attached = TryAttachProcess(process, out handle);
                ApplyClientAttachState(item, process, handle, attached);
                return item;
            }
            catch (Exception ex)
            {
                Log("[clients-ctor-error] " + Describe(ctor) + " :: " + ex.GetType().Name);
            }
        }

        try
        {
            var item = FormatterServices.GetUninitializedObject(itemType);
            IntPtr handle;
            var attached = TryAttachProcess(process, out handle);
            ApplyClientAttachState(item, process, handle, attached);
            return item;
        }
        catch
        {
            return null;
        }
    }

    private static object[] BuildCtorArgs(ConstructorInfo ctor, Process process)
    {
        var parameters = ctor.GetParameters();
        var args = new object[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            if (!TryBuildArg(parameters[i], process, out args[i]))
                return null;
        }

        return args;
    }

    private static int SyncClientAttachmentState(object collection, Process[] processes)
    {
        int attached = 0;
        var all = processes ?? Array.Empty<Process>();

        foreach (var item in EnumerateCollectionItems(collection))
        {
            var process = ResolveProcessForItem(item, all);
            if (process == null)
                continue;

            IntPtr handle;
            var ok = TryAttachProcess(process, out handle);
            ApplyClientAttachState(item, process, handle, ok);
            if (ok)
                attached++;
        }

        return attached;
    }

    private static IEnumerable<object> EnumerateCollectionItems(object collection)
    {
        var enumerable = collection as IEnumerable;
        if (enumerable == null)
            yield break;

        foreach (var item in enumerable)
        {
            if (item != null)
                yield return item;
        }
    }

    private static Process ResolveProcessForItem(object item, Process[] processes)
    {
        if (item == null)
            return null;

        var pid = ReadIntMember(item, "ProcessId") ?? ReadIntMember(item, "Pid") ?? ReadIntMember(item, "Id");
        if (pid.HasValue && pid.Value > 0)
        {
            try { return Process.GetProcessById(pid.Value); }
            catch { }
        }

        var clientName = ReadStringMember(item, "ClientName") ?? ReadStringMember(item, "ProcessName") ?? ReadStringMember(item, "Name");
        if (!string.IsNullOrWhiteSpace(clientName))
        {
            foreach (var process in processes)
            {
                var exe = process.ProcessName + ".exe";
                if (clientName.Equals(process.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                    clientName.Equals(exe, StringComparison.OrdinalIgnoreCase))
                {
                    return process;
                }
            }
        }

        if (processes.Length == 1)
            return processes[0];

        return null;
    }

    private static void ApplyClientAttachState(object item, Process process, IntPtr handle, bool attached)
    {
        if (item == null || process == null)
            return;

        SetPropertyIfPresent(item, "ClientName", process.ProcessName + ".exe");
        SetPropertyIfPresent(item, "ProcessName", process.ProcessName);
        SetPropertyIfPresent(item, "Status", attached ? "Attached" : "Detected");
        SetPropertyIfPresent(item, "User", "Roblox");
        SetPropertyIfPresent(item, "IsSelected", true);
        SetPropertyIfPresent(item, "Attached", attached);
        SetPropertyIfPresent(item, "IsAttached", attached);
        SetPropertyIfPresent(item, "Injected", attached);
        SetPropertyIfPresent(item, "IsInjected", attached);
        SetPropertyIfPresent(item, "Connected", attached);
        SetPropertyIfPresent(item, "CanExecute", attached);
        SetPropertyIfPresent(item, "ProcessId", process.Id);
        SetPropertyIfPresent(item, "Pid", process.Id);
        SetPropertyIfPresent(item, "Id", process.Id);
        SetPropertyIfPresent(item, "Handle", handle);
        SetPropertyIfPresent(item, "ProcessHandle", handle);
        SetPropertyIfPresent(item, "Process", process);
        SetFieldIfPresent(item, "dje_zHH7Z8RAH42J6R8Z_ejd", process.ProcessName + ".exe");
        SetFieldIfPresent(item, "dje_zSU6K4BP4_ejd", attached ? "Attached" : "Detected");
        SetFieldIfPresent(item, "dje_zA5S8C34V_ejd", "Roblox");
        SetFieldIfPresent(item, "dje_zGVCKK6XN37PFW3A_ejd", true);
        SetFieldIfPresent(item, "ProcessId", process.Id);
        SetFieldIfPresent(item, "Pid", process.Id);
        SetFieldIfPresent(item, "Handle", handle);
        SetFieldIfPresent(item, "ProcessHandle", handle);
        SetFieldIfPresent(item, "Process", process);

        foreach (var field in item.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (field.IsInitOnly)
                continue;

            try
            {
                if (field.FieldType == typeof(IntPtr))
                    field.SetValue(item, handle);
                else if (field.FieldType == typeof(Process))
                    field.SetValue(item, process);
                else if (field.FieldType == typeof(int) && NameLike(field.Name, "pid", "process", "id"))
                    field.SetValue(item, process.Id);
                else if ((field.FieldType == typeof(bool) || field.FieldType == typeof(bool?)) && NameLike(field.Name, "attach", "inject", "select", "active", "connect", "ready", "valid"))
                    field.SetValue(item, true);
                else if ((field.FieldType == typeof(bool) || field.FieldType == typeof(bool?)) && NameLike(field.Name, "detach", "close", "fail", "error", "invalid"))
                    field.SetValue(item, false);
                else if (field.FieldType == typeof(string) && NameLike(field.Name, "status"))
                    field.SetValue(item, attached ? "Attached" : "Detected");
                else if (field.FieldType == typeof(string) && NameLike(field.Name, "name"))
                    field.SetValue(item, process.ProcessName + ".exe");
            }
            catch
            {
            }
        }

        foreach (var prop in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (!prop.CanWrite || prop.GetIndexParameters().Length != 0)
                continue;

            try
            {
                var t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                var n = prop.Name;

                if (t == typeof(IntPtr))
                    prop.SetValue(item, handle, null);
                else if (t == typeof(Process))
                    prop.SetValue(item, process, null);
                else if (t == typeof(int) && NameLike(n, "pid", "process", "id"))
                    prop.SetValue(item, process.Id, null);
                else if ((t == typeof(bool)) && NameLike(n, "attach", "inject", "select", "active", "connect", "ready", "valid"))
                    prop.SetValue(item, true, null);
                else if ((t == typeof(bool)) && NameLike(n, "detach", "close", "fail", "error", "invalid"))
                    prop.SetValue(item, false, null);
                else if (t == typeof(string) && NameLike(n, "status"))
                    prop.SetValue(item, attached ? "Attached" : "Detected", null);
                else if (t == typeof(string) && NameLike(n, "name"))
                    prop.SetValue(item, process.ProcessName + ".exe", null);
            }
            catch
            {
            }
        }
    }

    private static bool TryBuildArg(ParameterInfo parameter, Process process, out object value)
    {
        value = null;
        var t = parameter.ParameterType;
        var underlying = Nullable.GetUnderlyingType(t) ?? t;

        if (underlying == typeof(Process))
            value = process;
        else if (underlying == typeof(IntPtr))
            value = process.MainWindowHandle;
        else if (underlying == typeof(string))
            value = process.ProcessName;
        else if (underlying == typeof(bool))
            value = true;
        else if (underlying == typeof(int))
            value = process.Id;
        else if (underlying == typeof(uint))
            value = (uint)process.Id;
        else if (underlying == typeof(long))
            value = (long)process.Id;
        else if (underlying == typeof(ulong))
            value = (ulong)process.Id;
        else if (underlying.IsEnum)
            value = Activator.CreateInstance(underlying);
        else if (underlying.IsArray)
            value = Array.CreateInstance(underlying.GetElementType() ?? typeof(object), 0);
        else if (parameter.HasDefaultValue)
            value = parameter.DefaultValue;
        else if (underlying.IsValueType)
            value = Activator.CreateInstance(underlying);
        else if (!underlying.IsAbstract && !underlying.IsInterface)
        {
            try
            {
                value = Activator.CreateInstance(underlying);
            }
            catch
            {
                return false;
            }
        }
        else
            return false;

        return true;
    }

    private static int ScoreCtor(ConstructorInfo ctor)
    {
        int score = 0;
        foreach (var p in ctor.GetParameters())
        {
            var t = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
            if (t == typeof(Process))
                score += 100;
            else if (t == typeof(IntPtr))
                score += 50;
            else if (t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong))
                score += 25;
            else if (t == typeof(string))
                score += 10;
            else if (t == typeof(bool))
                score += 6;
            else if (t.IsArray)
                score += 2;
        }
        return score;
    }

    private static void TryInvokeClientDiscovery(object dataContext, Process process, object sampleClientItem)
    {
        var type = dataContext.GetType();
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        foreach (var token in ClientRefreshTokens)
        {
            var method = methods.FirstOrDefault(m => m.MetadataToken == token);
            if (method == null)
                continue;

            object[] args = BuildMethodArgs(method, process, sampleClientItem);

            try
            {
                var result = method.Invoke(dataContext, args);
                var task = result as Task;
                if (task != null)
                {
                    task.ContinueWith(t =>
                    {
                        if (t.IsFaulted && t.Exception != null)
                            Log("[clients-refresh-error] " + Describe(method) + " :: " + t.Exception.GetBaseException().Message);
                    });
                }
                Log("[clients-refresh] invoked " + Describe(method));
            }
            catch (Exception ex)
            {
                Log("[clients-refresh-error] " + Describe(method) + " :: " + ex.GetType().Name);
            }
        }
    }

    private static object[] BuildMethodArgs(MethodInfo method, Process process, object sampleClientItem)
    {
        var ps = method.GetParameters();
        var args = new object[ps.Length];

        for (int i = 0; i < ps.Length; i++)
        {
            var t = Nullable.GetUnderlyingType(ps[i].ParameterType) ?? ps[i].ParameterType;
            if (t == typeof(bool))
                args[i] = true;
            else if (t == typeof(int))
                args[i] = process != null ? process.Id : 0;
            else if (t == typeof(uint))
                args[i] = process != null ? (uint)process.Id : 0u;
            else if (t == typeof(long))
                args[i] = process != null ? (long)process.Id : 0L;
            else if (t == typeof(ulong))
                args[i] = process != null ? (ulong)process.Id : 0UL;
            else if (t == typeof(string))
                args[i] = process != null ? process.ProcessName + ".exe" : string.Empty;
            else if (t == typeof(IntPtr))
            {
                IntPtr handle;
                args[i] = process != null && TryAttachProcess(process, out handle) ? handle : IntPtr.Zero;
            }
            else if (t == typeof(Process))
                args[i] = process ?? Process.GetCurrentProcess();
            else if (t.IsEnum)
                args[i] = Activator.CreateInstance(t);
            else if (t == typeof(CancellationToken))
                args[i] = CancellationToken.None;
            else if (t.IsArray)
                args[i] = Array.CreateInstance(t.GetElementType() ?? typeof(object), 0);
            else if (t == typeof(object))
                args[i] = sampleClientItem ?? (object)process;
            else if (sampleClientItem != null && t.IsInstanceOfType(sampleClientItem))
                args[i] = sampleClientItem;
            else if (process != null && t.IsInstanceOfType(process))
                args[i] = process;
            else if (ps[i].HasDefaultValue)
                args[i] = ps[i].DefaultValue;
            else if (t.IsValueType)
                args[i] = Activator.CreateInstance(t);
            else if (!t.IsAbstract && !t.IsInterface)
            {
                try { args[i] = Activator.CreateInstance(t); }
                catch { args[i] = null; }
            }
            else
            {
                args[i] = null;
            }
        }

        return args;
    }

    private static Process[] FindProcessesByName(string processName)
    {
        var results = new List<Process>();
        var seen = new HashSet<int>();

        try
        {
            foreach (var p in Process.GetProcessesByName(processName))
            {
                if (seen.Add(p.Id))
                    results.Add(p);
            }
        }
        catch (Exception ex)
        {
            Log("[clients-find-error] managed :: " + ex.GetType().Name);
        }

        foreach (var pid in FindProcessIdsBySnapshot(processName))
        {
            if (!seen.Add(pid))
                continue;

            try
            {
                results.Add(Process.GetProcessById(pid));
            }
            catch
            {
            }
        }

        return results.ToArray();
    }

    private static IEnumerable<int> FindProcessIdsBySnapshot(string processName)
    {
        var targetExe = processName + ".exe";
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
            yield break;

        try
        {
            var entry = new PROCESSENTRY32();
            entry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));

            if (!Process32First(snapshot, ref entry))
                yield break;

            do
            {
                if (string.Equals(entry.szExeFile, targetExe, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.szExeFile, processName, StringComparison.OrdinalIgnoreCase))
                {
                    yield return (int)entry.th32ProcessID;
                }
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static string ReadStringMember(object target, string name)
    {
        try
        {
            var type = target.GetType();
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanRead && prop.PropertyType == typeof(string))
                return prop.GetValue(target, null) as string;

            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null && field.FieldType == typeof(string))
                return field.GetValue(target) as string;
        }
        catch
        {
        }

        return null;
    }

    private static int? ReadIntMember(object target, string name)
    {
        try
        {
            var type = target.GetType();
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanRead)
            {
                var value = prop.GetValue(target, null);
                return ConvertToInt(value);
            }

            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
                return ConvertToInt(field.GetValue(target));
        }
        catch
        {
        }

        return null;
    }

    private static bool? ReadBoolMember(object target, string name)
    {
        try
        {
            var type = target.GetType();
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanRead)
            {
                var value = prop.GetValue(target, null);
                return ConvertToBool(value);
            }

            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
                return ConvertToBool(field.GetValue(target));
        }
        catch
        {
        }

        return null;
    }

    private static int? ConvertToInt(object value)
    {
        if (value == null)
            return null;

        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static bool? ConvertToBool(object value)
    {
        if (value == null)
            return null;

        try
        {
            return Convert.ToBoolean(value);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryAttachProcess(Process process, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        if (process == null)
            return false;

        lock (AttachedHandles)
        {
            IntPtr existing;
            if (AttachedHandles.TryGetValue(process.Id, out existing) && existing != IntPtr.Zero)
            {
                handle = existing;
                return true;
            }
        }

        handle = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, false, (uint)process.Id);
        if (handle == IntPtr.Zero)
            handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, (uint)process.Id);
        if (handle == IntPtr.Zero)
        {
            try
            {
                handle = process.Handle;
            }
            catch
            {
                handle = IntPtr.Zero;
            }
        }
        if (handle == IntPtr.Zero)
            return false;

        lock (AttachedHandles)
        {
            AttachedHandles[process.Id] = handle;
        }
        return true;
    }

    private static void CleanupDeadHandles(Process[] aliveProcesses)
    {
        var aliveIds = new HashSet<int>((aliveProcesses ?? Array.Empty<Process>()).Select(p => p.Id));
        lock (AttachedHandles)
        {
            foreach (var kv in AttachedHandles.ToList())
            {
                if (aliveIds.Contains(kv.Key))
                    continue;

                try { CloseHandle(kv.Value); } catch { }
                AttachedHandles.Remove(kv.Key);
            }
        }
    }

    private static void SetPropertyIfPresent(object target, string propertyName, object value)
    {
        try
        {
            var prop = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop == null || !prop.CanWrite)
                return;

            object converted;
            if (!TryConvertValue(value, prop.PropertyType, out converted))
                return;

            prop.SetValue(target, converted, null);
        }
        catch (Exception ex)
        {
            Log("[clients-set-error] " + propertyName + " :: " + ex.GetType().Name);
        }
    }

    private static void SetFieldIfPresent(object target, string fieldName, object value)
    {
        try
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null || field.IsInitOnly)
                return;

            object converted;
            if (!TryConvertValue(value, field.FieldType, out converted))
                return;

            field.SetValue(target, converted);
        }
        catch
        {
        }
    }

    private static bool NameLike(string source, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;

        foreach (var needle in needles)
        {
            if (!string.IsNullOrWhiteSpace(needle) &&
                source.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryConvertValue(object value, Type targetType, out object converted)
    {
        converted = null;

        if (targetType == typeof(object))
        {
            converted = value;
            return true;
        }

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (value == null)
        {
            if (!underlying.IsValueType || Nullable.GetUnderlyingType(targetType) != null)
            {
                converted = null;
                return true;
            }
            return false;
        }

        if (underlying.IsAssignableFrom(value.GetType()))
        {
            converted = value;
            return true;
        }

        try
        {
            if (underlying == typeof(IntPtr))
            {
                if (value is int i32)
                    converted = new IntPtr(i32);
                else if (value is long i64)
                    converted = new IntPtr(i64);
                else if (value is uint u32)
                    converted = new IntPtr(unchecked((int)u32));
                else if (value is ulong u64)
                    converted = new IntPtr(unchecked((long)u64));
                else if (value is string str && long.TryParse(str, out var parsed))
                    converted = new IntPtr(parsed);
                else
                    converted = new IntPtr(Convert.ToInt64(value));
                return true;
            }

            if (value is IntPtr ptr)
            {
                if (underlying == typeof(long))
                {
                    converted = ptr.ToInt64();
                    return true;
                }
                if (underlying == typeof(ulong))
                {
                    converted = unchecked((ulong)ptr.ToInt64());
                    return true;
                }
                if (underlying == typeof(int))
                {
                    converted = ptr.ToInt32();
                    return true;
                }
                if (underlying == typeof(uint))
                {
                    converted = unchecked((uint)ptr.ToInt32());
                    return true;
                }
                if (underlying == typeof(string))
                {
                    converted = ptr.ToInt64().ToString();
                    return true;
                }
            }

            if (underlying.IsEnum)
            {
                if (value is string)
                    converted = Enum.Parse(underlying, (string)value, true);
                else
                    converted = Enum.ToObject(underlying, value);
                return true;
            }

            converted = Convert.ChangeType(value, underlying);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Describe(ConstructorInfo ctor)
    {
        return ctor.DeclaringType.FullName + "::.ctor(" + string.Join(",", ctor.GetParameters().Select(p => p.ParameterType.Name).ToArray()) + ")";
    }

    private static int TryGetCount(object value)
    {
        if (value == null)
            return -1;

        var prop = value.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
        if (prop == null || !prop.CanRead || prop.PropertyType != typeof(int))
            return -1;

        try
        {
            return (int)prop.GetValue(value, null);
        }
        catch
        {
            return -1;
        }
    }


    private static void CollapseElement(FrameworkElement element)
    {
        if (element == null)
            return;

        var ui = element as UIElement;
        if (ui == null)
            return;

        ui.Visibility = Visibility.Collapsed;
        ui.IsEnabled = false;
        Log("[ui] collapsed " + DescribeElement(ui));
    }

    private static void ShowElement(FrameworkElement element)
    {
        if (element == null)
            return;

        var ui = element as UIElement;
        if (ui == null)
            return;

        ui.Visibility = Visibility.Visible;
        ui.IsEnabled = true;
        ui.Opacity = 1.0;
        Log("[ui] showed " + DescribeElement(ui));
    }

    private static void ForceHeaderButtonVisuals(FrameworkElement element)
    {
        if (element == null)
            return;

        var button = element as ButtonBase;
        if (button == null)
            return;

        button.Visibility = Visibility.Visible;
        button.IsEnabled = true;
        button.Opacity = 1.0;
        button.Width = double.IsNaN(button.Width) || button.Width < 24 ? 36 : button.Width;
        button.Height = double.IsNaN(button.Height) || button.Height < 18 ? 28 : button.Height;
        button.Background = new SolidColorBrush(Color.FromArgb(255, 28, 28, 28));
        button.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 90, 90, 90));
        button.BorderThickness = new Thickness(1);
        button.Foreground = new SolidColorBrush(Colors.White);
        button.Padding = new Thickness(0);

        if (string.IsNullOrWhiteSpace(button.Content as string))
        {
            if (IsNamed(button, "MinimizeT") || IsNamed(button, "MinimizeKT"))
                button.Content = "_";
            else if (IsNamed(button, "MaximizeT"))
                button.Content = "[]";
            else if (IsNamed(button, "CloseT") || IsNamed(button, "CloseKT"))
                button.Content = "X";
        }

        Panel.SetZIndex(button, 9999);
        Log("[ui] restyled " + DescribeElement(button));
    }

    private static Type[] SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
        catch { return new Type[0]; }
    }

    private static bool ReturnTrue()
    {
        return true;
    }

    private static void ReturnVoid()
    {
    }

    private static Task<bool> ReturnTrueTask()
    {
        return Task.FromResult(true);
    }

    private static void PatchMethod(MethodInfo source, MethodInfo destination)
    {
        RuntimeHelpers.PrepareMethod(source.MethodHandle);
        RuntimeHelpers.PrepareMethod(destination.MethodHandle);

        IntPtr srcPtr = source.MethodHandle.GetFunctionPointer();
        IntPtr dstPtr = destination.MethodHandle.GetFunctionPointer();

        byte[] jmp = new byte[12];
        jmp[0] = 0x48;
        jmp[1] = 0xB8;
        BitConverter.GetBytes(dstPtr.ToInt64()).CopyTo(jmp, 2);
        jmp[10] = 0xFF;
        jmp[11] = 0xE0;

        uint oldProtect;
        VirtualProtect(srcPtr, (UIntPtr)jmp.Length, 0x40, out oldProtect);
        Marshal.Copy(jmp, 0, srcPtr, jmp.Length);
        uint ignored;
        VirtualProtect(srcPtr, (UIntPtr)jmp.Length, oldProtect, out ignored);
    }

    private static void LogWindow(Window window)
    {
        var dataContextType = "<null>";
        if (window.DataContext != null && window.DataContext.GetType() != null)
            dataContextType = window.DataContext.GetType().FullName;
        Log("[window] " + window.GetType().FullName + " title=" + window.Title + " dataContext=" + dataContextType);
        foreach (var element in EnumerateVisuals(window).OfType<FrameworkElement>())
        {
            var id = AutomationProperties.GetAutomationId(element);
            var name = element.Name;
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
                continue;

            Log("[visual] " + DescribeElement(element));
        }
    }

    private static string Describe(MethodInfo method)
    {
        var declaringType = method.DeclaringType != null ? method.DeclaringType.FullName : "<null>";
        return method.MetadataToken.ToString("X8") + " " + declaringType + "::" + method.Name;
    }

    private static string DescribeElement(object element)
    {
        var fe = element as FrameworkElement;
        if (fe != null)
        {
            return fe.GetType().FullName +
                   " name=" + (string.IsNullOrWhiteSpace(fe.Name) ? "<none>" : fe.Name) +
                   " auto=" + (string.IsNullOrWhiteSpace(AutomationProperties.GetAutomationId(fe)) ? "<none>" : AutomationProperties.GetAutomationId(fe));
        }

        return element.GetType().FullName ?? "<unknown>";
    }

    private static string FormatValue(object value)
    {
        if (value == null)
            return "<null>";
        var s = value as string;
        if (s != null)
            return s;
        if (value is bool)
            return ((bool)value) ? "true" : "false";
        return value.ToString() ?? "<null-str>";
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(@"C:\Users\alvaro\Desktop\holacomotas");
            File.AppendAllText(@"C:\Users\alvaro\Desktop\holacomotas\Wave_Patched.log", message + Environment.NewLine);
        }
        catch
        {
        }
    }

    private const uint PROCESS_CREATE_THREAD = 0x0002;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
}
