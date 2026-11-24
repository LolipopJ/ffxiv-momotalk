using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;

namespace FFXIVMomoTalk.Windows;

internal sealed class WrappedPromptEditor
{
    private const int MaxPromptBytes = 4096;
    private const int UndoLimit = 99;

    private readonly List<EditState> undo = [];
    private readonly List<EditState> redo = [];
    private readonly ImGui.ImGuiInputTextCallbackPtrDelegate editCallback;
    private Layout layout = new(string.Empty, [], [], []);
    private TextPosition position;
    private byte[] inputBuffer = [];
    private string? fieldId;
    private ImFontPtr font;
    private float fontScale;
    private float wrapWidth;
    private bool needsLayout;
    private bool active;
    private bool readOnly;
    private bool undoRequested;
    private bool redoRequested;

    public WrappedPromptEditor() => editCallback = Edit;

    public unsafe bool Draw(string id, ref string text, Vector2 size, bool readOnly)
    {
        var wasActive = active && fieldId == id;
        var nextFont = ImGui.GetFont();
        var nextScale = ImGui.GetFontSize() / nextFont.FontSize;
        // Reserve space for the vertical scrollbar even before it appears.
        var nextWidth = MathF.Max(1f, MathF.Floor(size.X - ImGui.GetStyle().ScrollbarSize) - 1f);
        needsLayout |= font.Handle != nextFont.Handle || fontScale != nextScale || wrapWidth != nextWidth;
        font = nextFont;
        fontScale = nextScale;
        wrapWidth = nextWidth;
        this.readOnly = readOnly;

        if (fieldId != id || text != layout.Text)
        {
            fieldId = id;
            undo.Clear();
            redo.Clear();
            position = default;
            wasActive = false;
            SetLayout(text);
        }
        else if (needsLayout && (!wasActive || readOnly))
        {
            SetLayout(text);
        }

        var io = ImGui.GetIO();
        var ctrl = wasActive && io.KeyCtrl && !io.KeyAlt;
        undoRequested = !readOnly && ctrl && !io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.Z);
        redoRequested = !readOnly && ctrl && (ImGui.IsKeyPressed(ImGuiKey.Y)
            || (io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.Z)));
        var copy = ctrl && !io.KeyShift && (ImGui.IsKeyPressed(ImGuiKey.C, false) || ImGui.IsKeyPressed(ImGuiKey.Insert, false));
        var cut = !readOnly && ((ctrl && !io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.X, false))
            || (wasActive && io.KeyShift && !io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Delete, false)));
        var clipboardText = (copy || cut) && position.Start != position.End
            ? Encoding.UTF8.GetString(layout.Original.AsSpan(Math.Min(position.Start, position.End), Math.Abs(position.End - position.Start)))
            : null;

        // Each inserted line break consumes at most one byte per original UTF-8 byte.
        var capacity = 2 * Math.Max(MaxPromptBytes, layout.Original.Length) + 1;
        if (inputBuffer.Length < capacity)
            inputBuffer = new byte[capacity];
        layout.Display.CopyTo(inputBuffer, 0);
        inputBuffer[layout.Display.Length] = 0;

        // ImGui 1.88 edits visual lines, so undo and clipboard work on the original text here.
        var flags = ImGuiInputTextFlags.CallbackEdit | ImGuiInputTextFlags.CallbackAlways
            | ImGuiInputTextFlags.NoHorizontalScroll | ImGuiInputTextFlags.NoUndoRedo;
        if (readOnly)
            flags |= ImGuiInputTextFlags.ReadOnly;
        ImGui.InputTextMultiline(id, inputBuffer.AsSpan(), size, flags, editCallback);
        active = ImGui.IsItemActive();
        if (clipboardText is not null)
            ImGui.SetClipboardText(clipboardText);

        if (layout.Text == text)
            return false;

        text = layout.Text;
        return true;
    }

    private int Edit(ImGuiInputTextCallbackDataPtr data)
    {
        var displayPosition = new TextPosition(data.CursorPos, data.SelectionStart, data.SelectionEnd);
        var buffer = data.BufTextSpan;
        var displayChanged = !buffer.SequenceEqual(layout.Display);
        var previous = new EditState(layout.Text, position);
        var textEdited = displayChanged && data.EventFlag == ImGuiInputTextFlags.CallbackEdit;
        var edit = textEdited && !readOnly
            ? ReadEdit(buffer, displayPosition)
            : new EditState(layout.Text, displayPosition.ToOriginal(layout));

        if (undoRequested && undo.Count > 0)
        {
            Remember(redo, previous);
            edit = undo[^1];
            undo.RemoveAt(undo.Count - 1);
        }
        else if (redoRequested && redo.Count > 0)
        {
            Remember(undo, previous);
            edit = redo[^1];
            redo.RemoveAt(redo.Count - 1);
        }
        else if (edit.Text != layout.Text)
        {
            if (Encoding.UTF8.GetByteCount(edit.Text) > Math.Max(MaxPromptBytes, layout.Original.Length))
            {
                edit = previous;
            }
            else
            {
                Remember(undo, previous);
                redo.Clear();
            }
        }

        position = edit.Position;
        var updateLayout = edit.Text != layout.Text || needsLayout;
        if (updateLayout)
            SetLayout(edit.Text);

        if (!readOnly && (updateLayout || textEdited) && !buffer.SequenceEqual(layout.Display))
        {
            data.DeleteChars(0, data.BufTextLen);
            data.InsertChars(0, layout.Display);
            data.CursorPos = layout.ToDisplay(position.Cursor);
            data.SelectionStart = layout.ToDisplay(position.Start);
            data.SelectionEnd = layout.ToDisplay(position.End);
        }

        return 0;
    }

    private EditState ReadEdit(ReadOnlySpan<byte> edited, TextPosition displayPosition)
    {
        var previous = layout.Display.AsSpan();
        var newEnd = displayPosition.Cursor;
        var oldEnd = previous.Length - (edited.Length - newEnd);

        // A normal input operation leaves the bytes after the cursor intact. This anchors an
        // inserted real newline correctly when it is next to an identical visual newline.
        if (oldEnd < 0 || !previous[oldEnd..].SequenceEqual(edited[newEnd..]))
        {
            var sharedStart = previous.CommonPrefixLength(edited);
            oldEnd = previous.Length;
            newEnd = edited.Length;
            while (oldEnd > sharedStart && newEnd > sharedStart && previous[oldEnd - 1] == edited[newEnd - 1])
            {
                oldEnd--;
                newEnd--;
            }
        }

        var start = previous[..oldEnd].CommonPrefixLength(edited[..newEnd]);
        var originalStart = layout.ToOriginal(start);
        var originalEnd = layout.ToOriginal(oldEnd);
        var inserted = edited[start..newEnd];

        if (inserted.IsEmpty && originalStart == originalEnd)
        {
            // At a visual boundary the native editor removes only the inserted newline.
            var original = layout.Original.AsSpan();
            if (originalStart > 0 && ImGui.IsKeyPressed(ImGuiKey.Backspace))
            {
                Rune.DecodeLastFromUtf8(original[..originalStart], out _, out var bytes);
                originalStart -= bytes;
            }
            else if (originalEnd < original.Length && ImGui.IsKeyPressed(ImGuiKey.Delete))
            {
                Rune.DecodeFromUtf8(original[originalEnd..], out _, out var bytes);
                originalEnd += bytes;
            }

            var caret = new TextPosition(originalStart, originalStart, originalStart);
            return new EditState(Replace(originalStart, originalEnd, inserted), caret);
        }

        var rawDelta = inserted.Length - (originalEnd - originalStart);
        int ToOriginal(int offset) => offset <= start ? layout.ToOriginal(offset)
            : offset <= newEnd ? originalStart + offset - start
            : layout.ToOriginal(offset + oldEnd - newEnd) + rawDelta;
        var selection = new TextPosition(ToOriginal(displayPosition.Cursor), ToOriginal(displayPosition.Start), ToOriginal(displayPosition.End));
        return new EditState(Replace(originalStart, originalEnd, inserted), selection);
    }

    private string Replace(int start, int end, ReadOnlySpan<byte> inserted)
    {
        var original = layout.Original.AsSpan();
        var updated = new byte[original.Length - (end - start) + inserted.Length];
        original[..start].CopyTo(updated);
        inserted.CopyTo(updated.AsSpan(start));
        original[end..].CopyTo(updated.AsSpan(start + inserted.Length));
        return Encoding.UTF8.GetString(updated);
    }

    private void SetLayout(string text)
    {
        var original = Encoding.UTF8.GetBytes(text);
        var display = new byte[2 * original.Length];
        var breaks = new List<int>();
        var remaining = original.AsSpan();
        var written = 0;
        while (!remaining.IsEmpty)
        {
            var newline = remaining.IndexOf((byte)'\n');
            var line = newline < 0 ? remaining : remaining[..newline];
            while (!line.IsEmpty)
            {
                var take = Math.Clamp(font.CalcWordWrapPositionA(fontScale, line, wrapWidth), 1, line.Length);
                // An over-wide first glyph still belongs to one complete UTF-8 sequence.
                while (take < line.Length && (line[take] & 0xC0) == 0x80)
                    take++;

                line[..take].CopyTo(display.AsSpan(written));
                written += take;
                line = line[take..];
                if (!line.IsEmpty)
                {
                    breaks.Add(written);
                    display[written++] = (byte)'\n';
                }
            }

            if (newline < 0)
                break;

            display[written++] = (byte)'\n';
            remaining = remaining[(newline + 1)..];
        }

        layout = new Layout(text, original, display[..written], [.. breaks]);
        needsLayout = false;
    }

    private static void Remember(List<EditState> history, EditState state)
    {
        if (history.Count == UndoLimit)
            history.RemoveAt(0);
        history.Add(state);
    }

    private sealed record Layout(string Text, byte[] Original, byte[] Display, int[] Breaks)
    {
        public int ToOriginal(int offset)
        {
            offset = Math.Clamp(offset, 0, Display.Length);
            var index = Array.BinarySearch(Breaks, offset);
            return offset - (index >= 0 ? index : ~index);
        }

        public int ToDisplay(int offset)
        {
            foreach (var lineBreak in Breaks)
            {
                if (lineBreak > offset)
                    break;
                offset++;
            }

            return offset;
        }
    }

    private readonly record struct TextPosition(int Cursor, int Start, int End)
    {
        public TextPosition ToOriginal(Layout layout) =>
            new(layout.ToOriginal(Cursor), layout.ToOriginal(Start), layout.ToOriginal(End));
    }

    private readonly record struct EditState(string Text, TextPosition Position);
}
