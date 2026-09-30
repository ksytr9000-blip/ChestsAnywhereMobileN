using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ChestsAnywhereMobileNav;

public sealed class ModEntry : Mod
{
    private object? chestsApi;
    private Delegate? getOverlay;
    private MethodInfo? isOverlayModalMethod;

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
        this.chestsApi = this.Helper.ModRegistry.GetApi<object>("Pathoschild.ChestsAnywhere");
        if (this.chestsApi is null)
        {
            this.Monitor.Log("Chests Anywhere API wasn't found.", LogLevel.Error);
            return;
        }

        Type apiType = this.chestsApi.GetType();
        FieldInfo? getterField = apiType.GetField("GetOverlay", BindingFlags.Instance | BindingFlags.NonPublic);
        this.getOverlay = getterField?.GetValue(this.chestsApi) as Delegate;
        this.isOverlayModalMethod = apiType.GetMethod("IsOverlayModal", BindingFlags.Instance | BindingFlags.Public);

        if (this.getOverlay is null)
            this.Monitor.Log("Couldn't access Chests Anywhere's overlay getter. The installed Chests Anywhere version may have changed internally.", LogLevel.Error);
        else
            this.Monitor.Log("Mobile chest navigation ready.", LogLevel.Info);
    }

    private object? GetOverlay()
    {
        if (this.getOverlay is null)
            return null;

        try
        {
            return this.getOverlay.DynamicInvoke();
        }
        catch (Exception ex)
        {
            this.Monitor.LogOnce($"Couldn't read the Chests Anywhere overlay: {ex}", LogLevel.Error);
            return null;
        }
    }

    private bool IsOverlayModal()
    {
        if (this.chestsApi is null || this.isOverlayModalMethod is null)
            return false;

        try
        {
            return (bool)(this.isOverlayModalMethod.Invoke(this.chestsApi, null) ?? false);
        }
        catch
        {
            return false;
        }
    }

    private void OnRenderedActiveMenu(object? sender, RenderedActiveMenuEventArgs e)
    {
        if (!Context.IsWorldReady || this.GetOverlay() is null || this.IsOverlayModal())
            return;

        this.UpdateButtonBounds();

        this.DrawButton(e.SpriteBatch, this.prevChest, "< 상자");
        this.DrawButton(e.SpriteBatch, this.nextChest, "상자 >");
        this.DrawButton(e.SpriteBatch, this.prevCategory, "< 분류");
        this.DrawButton(e.SpriteBatch, this.nextCategory, "분류 >");
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || this.GetOverlay() is null || this.IsOverlayModal())
            return;

        // Android taps are normally exposed as MouseLeft. ControllerA is included as a harmless fallback.
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

        // Don't let the same tap activate an inventory slot underneath our button.
        this.Helper.Input.Suppress(e.Button);
        this.InvokeOverlayMethod(method);
    }

    private void InvokeOverlayMethod(string methodName)
    {
        object? overlay = this.GetOverlay();
        if (overlay is null)
            return;

        try
        {
            if (!this.CanNavigate(overlay))
            {
                Game1.playSound("cancel");
                return;
            }

            MethodInfo? method = FindMethod(overlay.GetType(), methodName);
            if (method is null)
            {
                this.Monitor.LogOnce($"Couldn't find Chests Anywhere method '{methodName}'.", LogLevel.Error);
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

    private bool CanNavigate(object overlay)
    {
        PropertyInfo? property = FindProperty(overlay.GetType(), "CanCloseChest");
        if (property is null)
            return true;

        return property.GetValue(overlay) is not bool canClose || canClose;
    }

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

    private static PropertyInfo? FindProperty(Type? type, string name)
    {
        while (type is not null)
        {
            PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (property is not null)
                return property;
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

        // Keep the buttons near the screen edges so they cover as few inventory slots as possible.
        int chestY = Math.Clamp(screenH / 2 - buttonH - gap / 2, margin, screenH - (buttonH * 2 + gap + margin));
        int categoryY = chestY + buttonH + gap;

        this.prevChest = new Rectangle(margin, chestY, buttonW, buttonH);
        this.nextChest = new Rectangle(screenW - margin - buttonW, chestY, buttonW, buttonH);
        this.prevCategory = new Rectangle(margin, categoryY, buttonW, buttonH);
        this.nextCategory = new Rectangle(screenW - margin - buttonW, categoryY, buttonW, buttonH);
    }

    private void DrawButton(SpriteBatch batch, Rectangle bounds, string text)
    {
        // Simple texture-free UI so this doesn't depend on any asset paths or other UI mods.
        Color fill = Color.Black * 0.72f;
        Color border = Color.White * 0.85f;
        batch.Draw(Game1.staminaRect, bounds, fill);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, bounds.Width, 2), border);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Bottom - 2, bounds.Width, 2), border);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, 2, bounds.Height), border);
        batch.Draw(Game1.staminaRect, new Rectangle(bounds.Right - 2, bounds.Y, 2, bounds.Height), border);

        Vector2 size = Game1.smallFont.MeasureString(text);
        Vector2 pos = new(
            bounds.X + (bounds.Width - size.X) / 2f,
            bounds.Y + (bounds.Height - size.Y) / 2f
        );
        batch.DrawString(Game1.smallFont, text, pos, Color.White);
    }
}
