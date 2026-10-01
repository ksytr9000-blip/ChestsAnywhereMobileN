using System;
using System.Collections.Generic;
using System.Reflection;
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
    private IChestsAnywhereApi? api;
    private string? lastMenuType;

    private Rectangle prevChest;
    private Rectangle nextChest;
    private Rectangle prevCategory;
    private Rectangle nextCategory;

    public override void Entry(IModHelper helper)
    {
        this.Monitor.Log("CAMN v0.3.0 ENTRY OK", LogLevel.Info);

        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.Display.Rendered += this.OnRendered;
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        this.api = this.Helper.ModRegistry.GetApi<IChestsAnywhereApi>("Pathoschild.ChestsAnywhere");
        this.Monitor.Log(this.api is null
            ? "CAMN v0.3.0: Chests Anywhere API not found; ItemGrabMenu fallback enabled."
            : "CAMN v0.3.0: Chests Anywhere API acquired.", LogLevel.Info);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        string type = Game1.activeClickableMenu?.GetType().FullName ?? "<none>";
        if (type == this.lastMenuType)
            return;

        this.lastMenuType = type;
        bool active = this.SafeOverlayActive();
        this.Monitor.Log($"CAMN MENU: {type}; overlay={active}", LogLevel.Info);
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

        return Game1.activeClickableMenu is ItemGrabMenu;
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
        object? overlay = this.ResolveNavigationObject();
        if (overlay is null)
        {
            this.Monitor.LogOnce("CAMN: buttons work, but Chests Anywhere navigation object wasn't found.", LogLevel.Error);
            Game1.playSound("cancel");
            return;
        }

        MethodInfo? method = FindMethod(overlay.GetType(), methodName);
        if (method is null)
        {
            this.Monitor.LogOnce($"CAMN: method {methodName} not found on {overlay.GetType().FullName}.", LogLevel.Error);
            Game1.playSound("cancel");
            return;
        }

        try
        {
            this.Monitor.Log($"CAMN INVOKE: {overlay.GetType().FullName}.{methodName}()", LogLevel.Info);
            method.Invoke(overlay, null);
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

    private object? ResolveNavigationObject()
    {
        HashSet<object> visited = new(ReferenceEqualityComparer.Instance);

        if (Game1.activeClickableMenu is not null)
        {
            object? found = this.ScanObject(Game1.activeClickableMenu, 0, visited);
            if (found is not null)
                return found;
        }

        if (this.api is not null)
        {
            object? found = this.ScanObject(this.api, 0, visited);
            if (found is not null)
                return found;
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name = assembly.GetName().Name ?? "";
            if (!name.Contains("ChestsAnywhere", StringComparison.OrdinalIgnoreCase))
                continue;

            object? found = this.ScanStaticMembers(assembly, visited);
            if (found is not null)
                return found;
        }

        return null;
    }

    private object? ScanObject(object obj, int depth, HashSet<object> visited)
    {
        if (depth > 6 || !visited.Add(obj))
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

    private object? ScanStaticMembers(Assembly assembly, HashSet<object> visited)
    {
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            List<Type> safe = new();
            foreach (Type? type in ex.Types)
                if (type is not null)
                    safe.Add(type);
            types = safe.ToArray();
        }

        foreach (Type type in types)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (FieldInfo field in type.GetFields(flags))
            {
                object? value;
                try { value = field.GetValue(null); }
                catch { continue; }

                object? found = this.InspectValue(value, 0, visited);
                if (found is not null)
                    return found;
            }

            foreach (PropertyInfo property in type.GetProperties(flags))
            {
                if (property.GetIndexParameters().Length != 0 || property.GetMethod is null)
                    continue;

                object? value;
                try { value = property.GetValue(null); }
                catch { continue; }

                object? found = this.InspectValue(value, 0, visited);
                if (found is not null)
                    return found;
            }
        }

        return null;
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
        int margin = 24;
        int gap = 16;
        int buttonW = Math.Clamp(screenW / 5, 160, 280);
        int buttonH = Math.Clamp(screenH / 11, 72, 110);
        int chestY = Math.Max(margin, screenH / 2 - buttonH - gap / 2);
        int categoryY = chestY + buttonH + gap;

        this.prevChest = new Rectangle(margin, chestY, buttonW, buttonH);
        this.nextChest = new Rectangle(screenW - margin - buttonW, chestY, buttonW, buttonH);
        this.prevCategory = new Rectangle(margin, categoryY, buttonW, buttonH);
        this.nextCategory = new Rectangle(screenW - margin - buttonW, categoryY, buttonW, buttonH);
    }

    private void DrawButton(SpriteBatch batch, Rectangle bounds, string text)
    {
        Color fill = new(0, 0, 0, 220);
        Color border = Color.Yellow;

        batch.Draw(Game1.staminaRect, bounds, fill);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, bounds.Width, 5), border);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Bottom - 5, bounds.Width, 5), border);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, 5, bounds.Height), border);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.Right - 5, bounds.Y, 5, bounds.Height), border);

        Vector2 size = Game1.smallFont.MeasureString(text);
        Vector2 pos = new(bounds.X + (bounds.Width - size.X) / 2f, bounds.Y + (bounds.Height - size.Y) / 2f);
        batch.DrawString(Game1.smallFont, text, pos, Color.White);
    }
}
