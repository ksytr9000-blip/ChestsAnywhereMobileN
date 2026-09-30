using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ChestsAnywhereMobileNav;

public interface IChestsAnywhereApi
{
    bool IsOverlayActive();
    bool IsOverlayModal();
}

public sealed class ModEntry : Mod
{
    private IChestsAnywhereApi? api;
    private object? rawApi;

    private Rectangle prevChest;
    private Rectangle nextChest;
    private Rectangle prevCategory;
    private Rectangle nextCategory;

    public override void Entry(IModHelper helper)
    {
        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.Display.RenderedActiveMenu += this.OnRenderedActiveMenu;
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        this.api = this.Helper.ModRegistry.GetApi<IChestsAnywhereApi>("Pathoschild.ChestsAnywhere");
        this.rawApi = this.Helper.ModRegistry.GetApi<object>("Pathoschild.ChestsAnywhere");

        if (this.api is null)
        {
            this.Monitor.Log("Couldn't get Chests Anywhere API.", LogLevel.Error);
            return;
        }

        this.Monitor.Log($"Mobile chest navigation loaded. API type: {this.rawApi?.GetType().FullName ?? "unknown"}", LogLevel.Info);
    }

    private bool IsOverlayActive()
    {
        try { return this.api?.IsOverlayActive() == true; }
        catch (Exception ex)
        {
            this.Monitor.LogOnce($"Couldn't read Chests Anywhere overlay state: {ex}", LogLevel.Error);
            return false;
        }
    }

    private bool IsOverlayModal()
    {
        try { return this.api?.IsOverlayModal() == true; }
        catch { return false; }
    }

    private void OnRenderedActiveMenu(object? sender, RenderedActiveMenuEventArgs e)
    {
        if (!Context.IsWorldReady || !this.IsOverlayActive() || this.IsOverlayModal())
            return;

        this.UpdateButtonBounds();

        this.DrawButton(e.SpriteBatch, this.prevChest, "< 상자");
        this.DrawButton(e.SpriteBatch, this.nextChest, "상자 >");
        this.DrawButton(e.SpriteBatch, this.prevCategory, "< 분류");
        this.DrawButton(e.SpriteBatch, this.nextCategory, "분류 >");
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || !this.IsOverlayActive() || this.IsOverlayModal())
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
        this.InvokeNavigation(method);
    }

    private void InvokeNavigation(string methodName)
    {
        object? overlay = this.ResolveOverlay();
        if (overlay is null)
        {
            this.Monitor.LogOnce("Chests Anywhere overlay is active, but I couldn't locate its internal overlay object.", LogLevel.Error);
            Game1.playSound("cancel");
            return;
        }

        try
        {
            MethodInfo? method = FindMethod(overlay.GetType(), methodName);
            if (method is null)
            {
                this.Monitor.LogOnce($"Couldn't find Chests Anywhere method '{methodName}' on {overlay.GetType().FullName}.", LogLevel.Error);
                Game1.playSound("cancel");
                return;
            }

            method.Invoke(overlay, null);
            Game1.playSound("shwip");
        }
        catch (TargetInvocationException ex)
        {
            this.Monitor.Log($"Chests Anywhere navigation failed ({methodName}): {ex.InnerException ?? ex}", LogLevel.Error);
            Game1.playSound("cancel");
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Chests Anywhere navigation failed ({methodName}): {ex}", LogLevel.Error);
            Game1.playSound("cancel");
        }
    }

    private object? ResolveOverlay()
    {
        if (this.rawApi is null)
            return null;

        HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
        return this.ScanObject(this.rawApi, 0, visited);
    }

    private object? ScanObject(object obj, int depth, HashSet<object> visited)
    {
        if (depth > 3 || !visited.Add(obj))
            return null;

        Type type = obj.GetType();
        if (HasNavigationMethods(type))
            return obj;

        for (Type? t = type; t is not null; t = t.BaseType)
        {
            foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                object? value;
                try { value = field.GetValue(obj); }
                catch { continue; }

                if (value is null || value is string || value.GetType().IsPrimitive)
                    continue;

                if (value is Delegate del)
                {
                    try
                    {
                        if (del.Method.GetParameters().Length == 0)
                        {
                            object? result = del.DynamicInvoke();
                            if (result is not null)
                            {
                                if (HasNavigationMethods(result.GetType()))
                                    return result;

                                object? nestedResult = this.ScanObject(result, depth + 1, visited);
                                if (nestedResult is not null)
                                    return nestedResult;
                            }
                        }
                    }
                    catch { }
                }

                object? nested = this.ScanObject(value, depth + 1, visited);
                if (nested is not null)
                    return nested;
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
            MethodInfo? method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
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

        int margin = 12;
        int gap = 10;
        int buttonW = Math.Clamp(screenW / 7, 105, 180);
        int buttonH = Math.Clamp(screenH / 14, 52, 82);

        int chestY = Math.Clamp(screenH / 2 - buttonH - gap / 2, margin, screenH - (buttonH * 2 + gap + margin));
        int categoryY = chestY + buttonH + gap;

        this.prevChest = new Rectangle(margin, chestY, buttonW, buttonH);
        this.nextChest = new Rectangle(screenW - margin - buttonW, chestY, buttonW, buttonH);
        this.prevCategory = new Rectangle(margin, categoryY, buttonW, buttonH);
        this.nextCategory = new Rectangle(screenW - margin - buttonW, categoryY, buttonW, buttonH);
    }

    private void DrawButton(SpriteBatch batch, Rectangle bounds, string text)
    {
        Color fill = Color.Black * 0.72f;
        Color border = Color.White * 0.85f;
        batch.Draw(Game1.staminaRect, bounds, fill);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, bounds.Width, 2), border);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Bottom - 2, bounds.Width, 2), border);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, 2, bounds.Height), border);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.Right - 2, bounds.Y, 2, bounds.Height), border);

        Vector2 size = Game1.smallFont.MeasureString(text);
        Vector2 pos = new(bounds.X + (bounds.Width - size.X) / 2f, bounds.Y + (bounds.Height - size.Y) / 2f);
        batch.DrawString(Game1.smallFont, text, pos, Color.White);
    }
}
