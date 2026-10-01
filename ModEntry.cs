using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace ChestsAnywhereMobileNav;

public interface IChestsAnywhereApi
{
    bool IsOverlayActive();
    bool IsOverlayModal();
}

public sealed class ModEntry : Mod
{
    private static ModEntry? Instance;
    private static WeakReference<object>? CapturedNavigationObject;

    private IChestsAnywhereApi? api;
    private Harmony? harmony;
    private string? lastMenuType;

    private Rectangle prevChest;
    private Rectangle nextChest;
    private Rectangle prevCategory;
    private Rectangle nextCategory;

    public override void Entry(IModHelper helper)
    {
        Instance = this;
        this.Monitor.Log("CAMN v0.4.0 ENTRY OK", LogLevel.Info);

        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.Display.Rendered += this.OnRendered;
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        this.api = this.Helper.ModRegistry.GetApi<IChestsAnywhereApi>("Pathoschild.ChestsAnywhere");
        this.Monitor.Log(this.api is null
            ? "CAMN: Chests Anywhere API not found; menu fallback enabled."
            : "CAMN: Chests Anywhere API acquired.", LogLevel.Info);

        this.TryPatchNavigationOverlayConstructors();
    }

    private void TryPatchNavigationOverlayConstructors()
    {
        try
        {
            this.harmony = new Harmony(this.ModManifest.UniqueID);
            MethodInfo postfix = AccessTools.Method(typeof(ModEntry), nameof(CaptureNavigationObject));

            int candidateTypes = 0;
            int patchedConstructors = 0;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types = SafeGetTypes(assembly);
                foreach (Type type in types)
                {
                    if (type == typeof(ModEntry))
                        continue;

                    string fullName = type.FullName ?? "";
                    if (!fullName.Contains("ChestsAnywhere", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!HasNavigationMethods(type))
                        continue;

                    candidateTypes++;
                    this.Monitor.Log($"CAMN NAV TYPE: {fullName}; abstract={type.IsAbstract}", LogLevel.Info);

                    if (type.IsAbstract)
                        continue;

                    ConstructorInfo[] ctors = type.GetConstructors(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    );

                    foreach (ConstructorInfo ctor in ctors)
                    {
                        try
                        {
                            this.harmony.Patch(ctor, postfix: new HarmonyMethod(postfix));
                            patchedConstructors++;
                        }
                        catch (Exception ex)
                        {
                            this.Monitor.Log($"CAMN: couldn't patch ctor {fullName}: {ex.Message}", LogLevel.Warn);
                        }
                    }
                }
            }

            this.Monitor.Log($"CAMN PATCH SUMMARY: navigationTypes={candidateTypes}, constructors={patchedConstructors}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"CAMN Harmony setup failed: {ex}", LogLevel.Error);
        }
    }

    private static void CaptureNavigationObject(object __instance)
    {
        try
        {
            if (__instance is null || !HasNavigationMethods(__instance.GetType()))
                return;

            CapturedNavigationObject = new WeakReference<object>(__instance);
            Instance?.Monitor.Log($"CAMN CAPTURED: {__instance.GetType().FullName}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Instance?.Monitor.Log($"CAMN capture failed: {ex.Message}", LogLevel.Warn);
        }
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        string type = Game1.activeClickableMenu?.GetType().FullName ?? "<none>";
        if (type == this.lastMenuType)
            return;

        this.lastMenuType = type;
        bool active = this.SafeOverlayActive();
        bool captured = TryGetCapturedNavigationObject(out object? nav);
        this.Monitor.Log($"CAMN MENU: {type}; overlay={active}; captured={captured}{(nav is null ? "" : $"; nav={nav.GetType().FullName}")}", LogLevel.Info);
    }

    private bool SafeOverlayActive()
    {
        try { return this.api?.IsOverlayActive() == true; }
        catch { return false; }
    }

    private bool ShouldShowButtons()
    {
        if (!Context.IsWorldReady)
            return false;

        if (this.SafeOverlayActive())
            return true;

        // Android SMAPI wraps the real chest menu in ItemGrabMenuFacade,
        // which derives from / behaves like ItemGrabMenu depending on build.
        object? menu = Game1.activeClickableMenu;
        if (menu is ItemGrabMenu)
            return true;

        string name = menu?.GetType().FullName ?? "";
        return name.Contains("ItemGrabMenuFacade", StringComparison.Ordinal);
    }

    private void OnRendered(object? sender, RenderedEventArgs e)
    {
        if (!this.ShouldShowButtons())
            return;

        this.UpdateButtonBounds();

        this.DrawButton(e.SpriteBatch, this.prevChest, "< CHEST");
        this.DrawButton(e.SpriteBatch, this.nextChest, "CHEST >");
        this.DrawButton(e.SpriteBatch, this.prevCategory, "< GROUP");
        this.DrawButton(e.SpriteBatch, this.nextCategory, "GROUP >");
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!this.ShouldShowButtons())
            return;

        if (e.Button != SButton.MouseLeft && e.Button != SButton.ControllerA)
            return;

        this.UpdateButtonBounds();
        Point p = new(Game1.getMouseX(), Game1.getMouseY());

        string? method = null;
        if (this.prevChest.Contains(p))
            method = "SelectPreviousChest";
        else if (this.nextChest.Contains(p))
            method = "SelectNextChest";
        else if (this.prevCategory.Contains(p))
            method = "SelectPreviousCategory";
        else if (this.nextCategory.Contains(p))
            method = "SelectNextCategory";

        if (method is null)
            return;

        this.Helper.Input.Suppress(e.Button);
        this.Monitor.Log($"CAMN TOUCH: {method}", LogLevel.Info);
        this.InvokeNavigation(method);
    }

    private void InvokeNavigation(string methodName)
    {
        object? nav = null;

        // Preferred path: capture the real Chests Anywhere overlay instance
        // at construction time, before Android hides it behind a facade.
        if (!TryGetCapturedNavigationObject(out nav))
        {
            // Fallback: scan the active menu tree in case a future build exposes it.
            HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
            if (Game1.activeClickableMenu is not null)
                nav = this.ScanObject(Game1.activeClickableMenu, 0, visited);
        }

        if (nav is null)
        {
            this.Monitor.Log("CAMN: no captured Chests Anywhere navigation object yet.", LogLevel.Error);
            Game1.playSound("cancel");
            return;
        }

        MethodInfo? method = FindMethod(nav.GetType(), methodName);
        if (method is null)
        {
            this.Monitor.Log($"CAMN: method {methodName} not found on {nav.GetType().FullName}.", LogLevel.Error);
            Game1.playSound("cancel");
            return;
        }

        try
        {
            this.Monitor.Log($"CAMN INVOKE: {nav.GetType().FullName}.{methodName}()", LogLevel.Info);
            method.Invoke(nav, null);
            Game1.playSound("shwip");
        }
        catch (TargetInvocationException ex)
        {
            this.Monitor.Log($"CAMN invoke failed: {ex.InnerException ?? ex}", LogLevel.Error);
            Game1.playSound("cancel");
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"CAMN invoke failed: {ex}", LogLevel.Error);
            Game1.playSound("cancel");
        }
    }

    private static bool TryGetCapturedNavigationObject(out object? nav)
    {
        nav = null;
        if (CapturedNavigationObject is null)
            return false;

        if (!CapturedNavigationObject.TryGetTarget(out object? value) || value is null)
            return false;

        if (!HasNavigationMethods(value.GetType()))
            return false;

        nav = value;
        return true;
    }

    private object? ScanObject(object obj, int depth, HashSet<object> visited)
    {
        if (depth > 8 || !visited.Add(obj))
            return null;

        Type type = obj.GetType();
        if (HasNavigationMethods(type))
            return obj;

        for (Type? t = type; t is not null; t = t.BaseType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            foreach (FieldInfo field in t.GetFields(flags))
            {
                object? value;
                try { value = field.GetValue(obj); }
                catch { continue; }

                object? found = this.InspectValue(value, depth, visited);
                if (found is not null)
                    return found;
            }

            foreach (PropertyInfo property in t.GetProperties(flags))
            {
                if (property.GetIndexParameters().Length != 0 || property.GetMethod is null)
                    continue;

                object? value;
                try { value = property.GetValue(obj); }
                catch { continue; }

                object? found = this.InspectValue(value, depth, visited);
                if (found is not null)
                    return found;
            }
        }

        return null;
    }

    private object? InspectValue(object? value, int depth, HashSet<object> visited)
    {
        if (value is null || value is string)
            return null;

        Type type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is decimal)
            return null;

        if (HasNavigationMethods(type))
            return value;

        return this.ScanObject(value, depth + 1, visited);
    }

    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(p => p is not null).Cast<Type>().ToArray();
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    private static bool HasNavigationMethods(Type type)
        => FindMethod(type, "SelectNextChest") is not null
        && FindMethod(type, "SelectPreviousChest") is not null;

    private static MethodInfo? FindMethod(Type? type, string name)
    {
        while (type is not null)
        {
            MethodInfo? method = type.GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null
            );
            if (method is not null)
                return method;
            type = type.BaseType;
        }
        return null;
    }

    private void UpdateButtonBounds()
    {
        int screenW = Game1.uiViewport.Width;
        int screenH = Game1.uiViewport.Height;
        int margin = 18;
        int gap = 10;
        int buttonW = Math.Clamp(screenW / 7, 130, 210);
        int buttonH = Math.Clamp(screenH / 14, 62, 88);

        // Keep the buttons lower and smaller so they obscure less of the chest UI.
        int categoryY = Math.Max(margin, screenH - margin - buttonH);
        int chestY = categoryY - gap - buttonH;

        this.prevChest = new Rectangle(margin, chestY, buttonW, buttonH);
        this.nextChest = new Rectangle(screenW - margin - buttonW, chestY, buttonW, buttonH);
        this.prevCategory = new Rectangle(margin, categoryY, buttonW, buttonH);
        this.nextCategory = new Rectangle(screenW - margin - buttonW, categoryY, buttonW, buttonH);
    }

    private void DrawButton(SpriteBatch batch, Rectangle bounds, string text)
    {
        Color fill = new(0, 0, 0, 205);
        Color border = Color.Yellow;

        batch.Draw(Game1.staminaRect, bounds, fill);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, bounds.Width, 4), border);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Bottom - 4, bounds.Width, 4), border);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, 4, bounds.Height), border);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.Right - 4, bounds.Y, 4, bounds.Height), border);

        Vector2 size = Game1.smallFont.MeasureString(text);
        float scale = Math.Min(1f, (bounds.Width - 12f) / Math.Max(1f, size.X));
        Vector2 pos = new(
            bounds.X + (bounds.Width - size.X * scale) / 2f,
            bounds.Y + (bounds.Height - size.Y * scale) / 2f
        );
        batch.DrawString(Game1.smallFont, text, pos, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }
}
