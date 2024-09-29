using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CelesteStudio.Communication;
using CelesteStudio.Util;
using StudioCommunication.Util;
using System.Diagnostics;
using System.Threading.Tasks;

namespace CelesteStudio.Editing;

public struct CaretPosition(int row = 0, int col = 0) {
    public int Row = row, Col = col;

    public static bool operator ==(CaretPosition lhs, CaretPosition rhs) => lhs.Row == rhs.Row && lhs.Col == rhs.Col;
    public static bool operator !=(CaretPosition lhs, CaretPosition rhs) => !(lhs == rhs);
    public static bool operator >(CaretPosition lhs, CaretPosition rhs) => lhs.Row > rhs.Row || (lhs.Row == rhs.Row && lhs.Col > rhs.Col);
    public static bool operator <(CaretPosition lhs, CaretPosition rhs) => lhs.Row < rhs.Row || (lhs.Row == rhs.Row && lhs.Col < rhs.Col);
    public static bool operator >=(CaretPosition lhs, CaretPosition rhs) => lhs > rhs || lhs == rhs;
    public static bool operator <=(CaretPosition lhs, CaretPosition rhs) => lhs < rhs || lhs == rhs;

    public override string ToString() => $"{Row}:{Col}";
    public override bool Equals(object? obj) => obj is CaretPosition other && Row == other.Row && Col == other.Col;
    public override int GetHashCode() => HashCode.Combine(Row, Col);
}

public enum CaretMovementType {
    None,
    CharLeft,
    CharRight,
    WordLeft,
    WordRight,
    LineUp,
    LineDown,
    // Labels include #'s with no space and breakpoints
    LabelUp,
    LabelDown,
    PageUp,
    PageDown,
    LineStart,
    LineEnd,
    DocumentStart,
    DocumentEnd,
}

public struct Selection() {
    public CaretPosition Start = new(), End = new();

    public CaretPosition Min => Start < End ? Start : End;
    public CaretPosition Max => Start < End ? End : Start;
    public bool Empty => Start == End;

    public void Clear() => Start = End = new();
    public void Normalize() {
        // Ensures that Start <= End
        if (Start > End) {
            (Start, End) = (End, Start);
        }
    }
}

public class Anchor {
    public int Row;
    public int MinCol, MaxCol;

    public object? UserData;
    public Action? OnRemoved;

    public bool IsPositionInside(CaretPosition position) => position.Row == Row && position.Col >= MinCol && position.Col <= MaxCol;
    public Anchor Clone() => new() { Row = Row, MinCol = MinCol, MaxCol = MaxCol, UserData = UserData, OnRemoved = OnRemoved };
}

public class Document : IDisposable {
    // Unify all TASes to use a single line separator
    public const char NewLine = '\n';

    // Used while the document isn't saved yet
    public static string ScratchFile => Path.Combine(Settings.BaseConfigPath, ".temp.tas");

    // Should only be used while an actual document is being loaded
    public static readonly Document Dummy = new(null);

    public CaretPosition Caret = new();
    public Selection Selection = new();

    public readonly string FilePath;
    public string FileName => Path.GetFileName(FilePath);
    public string BackupDirectory {
        get {
            if (string.IsNullOrWhiteSpace(FilePath))
                return string.Empty;

            var backupBaseDir = Path.Combine(Settings.BaseConfigPath, "Backups");
            bool isBackupFile = Directory.GetParent(FilePath) is { } dir && dir.Parent?.FullName == backupBaseDir;

            // Bit-cast to uint to avoid negative numbers
            uint hash;
            unchecked { hash = (uint)FilePath.GetStableHashCode(); }

            string backupSubDir = isBackupFile
                ? Directory.GetParent(FilePath)!.FullName
                : $"{FileName}_{hash}";

            return Path.Combine(backupBaseDir, backupSubDir);
        }
    }

    private readonly UndoStack undoStack = new();

    private List<string> CurrentLines;
    public List<string> Lines => CurrentLines;

    /// An anchor is a part of the document, which will move with the text its placed on.
    /// They can hold arbitrary user data.
    /// As their text gets edited, they will grow / shrink in size or removed entirely.
    private Dictionary<int, List<Anchor>> CurrentAnchors = [];
    public IEnumerable<Anchor> Anchors => CurrentAnchors.SelectMany(pair => pair.Value);

    public string Text => string.Join(NewLine, CurrentLines);
    public bool Dirty { get; private set; }

    // Ignore file-watcher events for 10ms after saving, to avoid triggering ourselves
    // This is probably way higher than it needs to be, to avoid potential issues with slow drives
    private static readonly TimeSpan FileReloadTimeout = TimeSpan.FromSeconds(3);
    private DateTime lastFileSave = DateTime.UtcNow;

    private readonly FileSystemWatcher watcher;

    private readonly Stack<QueuedUpdate> updateStack = [];

    /// Reports insertions and deletions of the document
    public event Action<Document, Dictionary<int, string>, Dictionary<int, string>> TextChanged = (doc, _, _) => {
        if (Settings.Instance.AutoSave) {
            doc.Save();
        } else {
            doc.Dirty = true;
        }
    };
    private void OnTextChanged(Dictionary<int, string> insertions, Dictionary<int, string> deletions) => TextChanged.Invoke(this, insertions, deletions);

    private Document(string? filePath) {
        FilePath = filePath ?? string.Empty;

        bool validPath = !string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath);
        CurrentLines = !validPath ? [] : File.ReadAllLines(filePath!).ToList();

        if (validPath) {
            // Save with the new line endings
            Save();
        }

        watcher = new FileSystemWatcher();
        if (filePath != null) {
            watcher.Changed += OnFileChanged;
            watcher.Path = Path.GetDirectoryName(filePath) ?? string.Empty;
            watcher.Filter = Path.GetFileName(filePath);
            watcher.EnableRaisingEvents = true;
        }

        CommunicationWrapper.LinesUpdated += OnLinesUpdated;
    }

    ~Document() => Dispose();
    public void Dispose() {
        GC.SuppressFinalize(this);

        watcher.Dispose();
        CommunicationWrapper.LinesUpdated -= OnLinesUpdated;
    }

    public static Document? Load(string path) {
        try {
            return new Document(path);
        } catch (Exception e) {
            Console.Error.WriteLine(e);
        }

        return null;
    }

    public void Save() {
        if (string.IsNullOrWhiteSpace(FilePath)) {
            return;
        }

        try {
            lastFileSave = DateTime.UtcNow;
            File.WriteAllText(FilePath, Text);
            Dirty = false;

            if (Settings.Instance.AutoBackupEnabled && !string.IsNullOrWhiteSpace(FilePath))
                CreateBackup();
        } catch (Exception e) {
            Console.Error.WriteLine(e);
        }
    }

    private void CreateBackup() {
        var backupDir = BackupDirectory;
        if (!Directory.Exists(backupDir))
            Directory.CreateDirectory(backupDir);

        string[] files = Directory.GetFiles(backupDir)
            // Sort for oldest first
            .OrderBy(file => File.GetLastWriteTimeUtc(file).Ticks)
            .ToArray();

        if (files.Length > 0) {
            var lastFileTime = File.GetLastWriteTimeUtc(files.Last());

            // Wait until next interval
            if (Settings.Instance.AutoBackupRate > 0 && lastFileTime.AddMinutes(Settings.Instance.AutoBackupRate) >= DateTime.UtcNow) {
                return;
            }

            // Delete the oldest backups until the desired count is reached
            if (Settings.Instance.AutoBackupCount > 0 && files.Length >= Settings.Instance.AutoBackupCount) {
                foreach (string path in files.Take(files.Length - Settings.Instance.AutoBackupCount + 1)) {
                    File.Delete(path);
                }
            }
        }

        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(FilePath);
        string backupFileName = Path.Combine(backupDir, fileNameWithoutExtension + DateTime.Now.ToString("_yyyy-MM-dd_HH-mm-ss-fff") + ".tas");
        File.Copy(FilePath, Path.Combine(backupDir, backupFileName));
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e) {
        if (DateTime.UtcNow - lastFileSave <= FileReloadTimeout) {
            // Avoid events by us saving
            return;
        }
        Console.WriteLine($"Change: {e.FullPath} - {e.ChangeType}");

        // Need to try multiple times, since the file might still be used by other processes
        // The file might also just be temporarily be deleted and re-created by an external tool
        var newLines = Task.Run(async () => {
            const int numberOfRetries = 3;
            const int delayOnRetry = 1000;
            const int ERROR_SHARING_VIOLATION = unchecked((int)0x80070020);

            for (int i = 1; i <= numberOfRetries; i++) {
                try {
                    return await File.ReadAllLinesAsync(FilePath);
                }
                catch (IOException ex) when (ex.HResult == ERROR_SHARING_VIOLATION || ex is FileNotFoundException) {
                    await Task.Delay(delayOnRetry).ConfigureAwait(false);
                } catch (Exception ex) {
                    // Something else failed; abort reload
                    Console.WriteLine($"Failed to reload file: {ex}");
                    return null;
                }
            }

            return null;
        }).Result;

        if (newLines == null) {
            // Failed to read the file
            return;
        }

        // Check for changes to avoid pushing empty undo-states
        bool changes = false;
        if (CurrentLines.Count != newLines.Length) {
            changes = true;
        } else {
            for (int i = 0; i < newLines.Length; i++) {
                if (CurrentLines[i] != newLines[i]) {
                    changes = true;
                    break;
                }
            }
        }

        if (!changes) {
            return; // Nothing to do
        }

        // No unsaved changes are discarded since this is just a regular, undo-able update
        using var __ = Update();
        using var patch = new Patch(this);

        patch.DeleteRange(0, CurrentLines.Count);
        patch.InsertRange(0, newLines);
    }

    private void OnLinesUpdated(Dictionary<int, string> newLines) {
        foreach ((int lineNum, string newText) in newLines) {
            if (lineNum < 0 || lineNum >= CurrentLines.Count)
                continue;

            CurrentLines[lineNum] = newText;
            // Cannot properly update anchors
            CurrentAnchors.Remove(lineNum);
        }
    }

    #region Anchors

    public void AddAnchor(Anchor anchor) {
        CurrentAnchors.TryAdd(anchor.Row, []);
        CurrentAnchors[anchor.Row].Add(anchor);
    }
    public void RemoveAnchor(Anchor anchor) {
        foreach ((int _, List<Anchor> list) in CurrentAnchors) {
            if (list.Remove(anchor)) {
                break;
            }
        }
    }
    public void RemoveAnchorsIf(Predicate<Anchor> predicate) {
        foreach ((int _, List<Anchor> list) in CurrentAnchors) {
            list.RemoveAll(predicate);
        }
    }
    public Anchor? FindFirstAnchor(Func<Anchor, bool> predicate) {
        foreach ((int _, List<Anchor> list) in CurrentAnchors) {
            if (list.FirstOrDefault(predicate) is { } anchor) {
                return anchor;
            }
        }
        return null;
    }
    public IEnumerable<Anchor> FindAnchors(Func<Anchor, bool> predicate) {
        foreach ((int _, List<Anchor> list) in CurrentAnchors) {
            foreach (var anchor in list) {
                if (predicate(anchor)) {
                    yield return anchor;
                }
            }
        }
    }

    #endregion

    #region Text Manipulation

    /// Ring buffer containing patches to go between undo-states
    public sealed class UndoStack(int bufferSize = 256) {
        /// Represents the state of the document at a certain point in time, in relation to other entries
        public record struct Entry(Patch[] patches, Dictionary<int, List<Anchor>> anchors, CaretPosition caret);

        private int curr = 0, head = 0, tail = 0;
        private readonly Entry[] stack = new Entry[bufferSize];

        public void Push(QueuedUpdate update) {
            stack[head] = new Entry(
                update.Patches.ToArray(),
                update.Document.CurrentAnchors.ToDictionary(e => e.Key, e => e.Value.Select(anchor => anchor.Clone()).ToList()),
                update.Document.Caret
            );

            head = curr = (curr + 1).Mod(bufferSize);
            if (head == tail) {
                tail = (tail + 1).Mod(bufferSize); // Discard the oldest entry
            }
        }

        /// Returns an entry containing the data to go to the previous one
        public Entry? Undo() {
            if (curr == tail) {
                return null;
            }

            curr = (curr - 1).Mod(bufferSize);
            return new Entry(
                stack[curr].patches.Reverse().ToArray(),
                stack[curr].anchors.ToDictionary(e => e.Key, e => e.Value.Select(anchor => anchor.Clone()).ToList()),
                stack[curr].caret);
        }
        /// Returns an entry containing the data to go to the next one
        public Entry? Redo() {
            if (curr == head) {
                return null;
            }

            curr = (curr + 1).Mod(bufferSize);
            return stack[curr];
        }
    }

    /// Represents a big, undo-able, modification of the document
    public sealed class QueuedUpdate : IDisposable {
        public readonly List<Patch> Patches = [];
        public readonly Document Document;

        private readonly bool raiseEvents;

        public QueuedUpdate(Document document, bool raiseEvents) {
            Document = document;
            this.raiseEvents = raiseEvents;

            document.updateStack.Push(this);
        }

        public void Dispose() {
            Document.updateStack.Pop();

            if (Patches.Count == 0) {
                return;
            }

            if (Document.updateStack.Count == 0) {
                if (raiseEvents) {
                    var totalPatch = Patches.Aggregate(Patch.Merge);
                    if (totalPatch.Insertions.Count == 0 && totalPatch.Deletions.Count == 0) {
                        return;
                    }

                    Document.undoStack.Push(this);
                    Document.OnTextChanged(totalPatch.Insertions, totalPatch.Deletions);
                }
            } else {
                Document.updateStack.Peek().Patches.AddRange(Patches);
            }
        }
    }

    /// Represents a small modification of the document with insertions and deletions
    public readonly struct Patch(Document document, QueuedUpdate? update = null) : IDisposable {
        // Insertions are at the lines where the new text will be
        // Deletions are at the lines where the line currently is
        public readonly Dictionary<int, string> Insertions = [], Deletions = [];

        private readonly QueuedUpdate update = update ?? document.updateStack.Peek();

        public void Insert(int row, string line) {
            Insertions[row] = line;
        }
        public void InsertRange(int row, IEnumerable<string> lines) {
            foreach (string line in lines) {
                Insertions[row++] = line;
            }
        }

        public void Delete(int row) {
            if (!Insertions.Remove(row)) {
                Deletions.Add(row, document.CurrentLines[row]);
            }
        }
        public void DeleteRange(int minRow, int maxRow) {
            for (int row = minRow; row <= maxRow; row++) {
                Delete(row);
            }
        }

        public void Modify(int row, string line) {
            if (!Insertions.Remove(row)) {
                Deletions.Add(row, document.CurrentLines[row]);
            }
            Insertions[row] = line;
        }

        public void Dispose() {
            // Clean-up no-ops
            var commonRows = Insertions.Keys.Intersect(Deletions.Keys);
            foreach (var row in commonRows) {
                if (Insertions[row] == Deletions[row]) {
                    Insertions.Remove(row);
                    Deletions.Remove(row);
                }
            }
            if (Insertions.Count == 0 && Deletions.Count == 0) {
                return;
            }

            document.ApplyPatch(Insertions, Deletions);
            update.Patches.Add(this);
        }

        public Patch Copy() {
            var patch = new Patch(document, update);
            foreach ((int row, string line) in Insertions) {
                patch.Insertions[row] = line;
            }
            foreach ((int row, string line) in Deletions) {
                patch.Deletions[row] = line;
            }
            return patch;
        }

        public static Patch Merge(Patch a, Patch b) {
            var result = a.Copy();

            foreach ((int row, string line) in b.Deletions) {
                if (!result.Insertions.Remove(row)) {
                    result.Deletions.Add(row, line);
                }

                foreach ((int insertionRow, string insertionLine) in result.Insertions) {
                    if (insertionRow >= row) {
                        result.Insertions[insertionRow - 1] = insertionLine;
                        result.Insertions.Remove(insertionRow);
                    }
                }
            }

            foreach ((int row, string line) in b.Insertions) {
                result.Insertions.Add(row, line);
            }

            return result;
        }
    }

    /// Starts a new update, if there isn't one active already
    public QueuedUpdate Update(bool raiseEvents = true) => new(this, raiseEvents);

    private void ApplyPatch(Dictionary<int, string> insertions, Dictionary<int, string> deletions) {
        // Create a mapping of patch rows to actual rows, while editing the lines
        var realDeletionsRows = deletions.Keys.ToDictionary(row => row);
        var realInsertionRows = insertions.Keys.ToDictionary(row => row);

        foreach ((int row, _) in deletions) {
            CurrentLines.RemoveAt(realDeletionsRows[row]);

            // Shift following lines
            foreach ((int patchRow, int realRow) in realDeletionsRows) {
                if (patchRow > row) {
                    realDeletionsRows[patchRow] = realRow - 1;
                }
            }
            foreach ((int patchRow, int realRow) in realInsertionRows) {
                if (patchRow > row) {
                    realDeletionsRows[patchRow] = realRow - 1;
                }
            }
        }
        foreach ((int row, string line) in insertions) {
            CurrentLines.Insert(realInsertionRows[row], line);
        }
    }

    public void Undo() {
        if (undoStack.Undo() is not { } entry) {
            return;
        }

        // Un-apply patches (already reversed)
        foreach (var patch in entry.patches) {
            ApplyPatch(patch.Deletions, patch.Insertions);
        }

        CurrentAnchors = entry.anchors;
        Caret = entry.caret;
    }
    public void Redo() {
        if (undoStack.Redo() is not { } entry) {
            return;
        }

        // Re-apply patches
        foreach (var patch in entry.patches) {
            ApplyPatch(patch.Insertions, patch.Deletions);
        }

        CurrentAnchors = entry.anchors;
        Caret = entry.caret;
    }

    public void Insert(string text) => Caret = Insert(Caret, text);
    public CaretPosition Insert(CaretPosition pos, string text) {
        string[] newLines = text.ReplaceLineEndings(NewLine.ToString()).SplitDocumentLines();
        if (newLines.Length == 0) {
            return pos;
        }

        using var patch = new Patch(this);

        if (newLines.Length == 1) {
            // Update anchors
            if (CurrentAnchors.TryGetValue(pos.Row, out var anchors)) {
                foreach (var anchor in anchors) {
                    if (pos.Col < anchor.MinCol) {
                        anchor.MinCol += text.Length;
                    }
                    if (pos.Col <= anchor.MaxCol) {
                        anchor.MaxCol += text.Length;
                    }
                }
            }

            patch.Modify(pos.Row, CurrentLines[pos.Row].Insert(pos.Col, text));
            pos.Col += text.Length;
        } else {
            // Move anchors below down
            for (int row = CurrentLines.Count - 1; row > pos.Row; row--) {
                if (CurrentAnchors.Remove(row, out var aboveAnchors)) {
                    CurrentAnchors[row + newLines.Length - 1] = aboveAnchors;
                    foreach (var anchor in aboveAnchors) {
                        anchor.Row += newLines.Length - 1;
                    }
                }
            }
            // Update anchors
            if (CurrentAnchors.TryGetValue(pos.Row, out var anchors)) {
                int newRow = pos.Row + newLines.Length - 1;

                CurrentAnchors.TryAdd(newRow, []);
                var newAnchors = CurrentAnchors[newRow];

                for (int i = anchors.Count - 1; i >= 0; i = Math.Min(i - 1, anchors.Count - 1)) {
                    var anchor = anchors[i];

                    // Invalidate in between
                    if (pos.Col >= anchor.MinCol && pos.Col <= anchor.MaxCol) {
                        anchor.OnRemoved?.Invoke();
                        anchors.Remove(anchor);
                        continue;
                    }
                    if (pos.Col >= anchor.MinCol) {
                        continue;
                    }

                    int offset = anchor.MinCol - pos.Col;
                    int len = anchor.MaxCol - anchor.MinCol;
                    anchor.MinCol = offset + newLines[0].Length;
                    anchor.MaxCol = offset + len + newLines[0].Length;
                    anchor.Row = newRow;
                    anchors.Remove(anchor);
                    newAnchors.Add(anchor);
                }
            }

            string left  = CurrentLines[pos.Row][..pos.Col];
            string right = CurrentLines[pos.Row][pos.Col..];

            patch.Modify(pos.Row, left + newLines[0]);
            patch.InsertRange(pos.Row + 1, newLines[1..]);
            pos.Row += newLines.Length - 1;
            pos.Col = newLines[^1].Length;
            patch.Modify(pos.Row, CurrentLines[pos.Row] + right);
        }

        return pos;
    }

    public void InsertLineAbove(string text) => InsertLine(Caret.Row, text);
    public void InsertLineBelow(string text) => InsertLine(Caret.Row + 1, text);
    public void InsertLine(int row, string text) {
        using var patch = new Patch(this);

        string[] newLines = text.SplitDocumentLines();
        if (newLines.Length == 0) {
            patch.Insert(row, string.Empty);
        } else {
            patch.InsertRange(row, newLines);
        }

        if (Caret.Row >= row) {
            int newLineCount = text.Count(c => c == NewLine) + 1;
            Caret.Row += newLineCount;
        }
    }
    public void InsertLines(int row, string[] newLines) {
        using var patch = new Patch(this);

        patch.InsertRange(row, newLines);

        if (Caret.Row >= row) {
            Caret.Row += newLines.Length;
        }
    }

    public void ReplaceLine(int row, string text) {
        string[] newLines = text.SplitDocumentLines();
        ReplaceLines(row, newLines);
    }

    public void ReplaceLines(int row, string[] newLines) {
        using var patch = new Patch(this);

        switch (newLines.Length)
        {
            case 0:
                patch.Modify(row, string.Empty);
                break;
            case 1:
                patch.Modify(row, newLines[0]);
                break;
            default:
                patch.Modify(row, newLines[0]);
                patch.InsertRange(row + 1, newLines[1..]);
                break;
        }

        if (Caret.Row >= row) {
            int newLineCount = newLines.Length > 0 ? newLines.Length-1 : 0;
            Caret.Row += newLineCount;
        }
    }

    public void SwapLines(int rowA, int rowB) {
        using var patch = new Patch(this);

        patch.Modify(rowA, CurrentLines[rowB]);
        patch.Modify(rowB, CurrentLines[rowA]);
    }

    public void RemoveRange(CaretPosition start, CaretPosition end) {
        if (start.Row == end.Row) {
            RemoveRangeInLine(start.Row, start.Col, end.Col);
            return;
        }

        using var patch = new Patch(this);

        if (start > end) {
            (end, start) = (start, end);
        }

        List<Anchor>? anchors;
        // Invalidate in between
        for (int row = start.Row; row <= end.Row; row++) {
            if (CurrentAnchors.TryGetValue(row, out anchors)) {
                for (int i = anchors.Count - 1; i >= 0; i = Math.Min(i - 1, anchors.Count - 1)) {
                    var anchor = anchors[i];

                    if (row == start.Row && anchor.MaxCol <= start.Col ||
                        row == end.Row && anchor.MinCol <= end.Col)
                    {
                        continue;
                    }

                    anchor.OnRemoved?.Invoke();
                    anchors.Remove(anchor);
                }
            }
        }
        // Update anchors
        if (CurrentAnchors.TryGetValue(end.Row, out anchors)) {
            CurrentAnchors.TryAdd(start.Row, []);
            var newAnchors = CurrentAnchors[start.Row];

            for (int i = anchors.Count - 1; i >= 0; i = Math.Min(i - 1, anchors.Count - 1)) {
                var anchor = anchors[i];

                int offset = anchor.MinCol - end.Col;
                int len = anchor.MaxCol - anchor.MinCol;
                anchor.MinCol = offset + start.Col;
                anchor.MaxCol = offset + len + start.Col;
                anchor.Row = start.Row;
                anchors.Remove(anchor);
                newAnchors.Add(anchor);
            }
        }
        // Move anchors below up
        for (int row = end.Row + 1; row < CurrentLines.Count; row++) {
            if (CurrentAnchors.Remove(row, out var aboveAnchors)) {
                CurrentAnchors[row - (end.Row - start.Row)] = aboveAnchors;
                foreach (var anchor in aboveAnchors) {
                    anchor.Row -= end.Row - start.Row;
                }
            }
        }

        patch.Modify(start.Row, CurrentLines[start.Row][..start.Col] + CurrentLines[end.Row][end.Col..]);
        patch.DeleteRange(start.Row + 1, end.Row);
    }

    public void RemoveRangeInLine(int row, int startCol, int endCol) {
        using var patch = new Patch(this);

        if (startCol > endCol) {
            (endCol, startCol) = (startCol, endCol);
        }

        // Update anchors
        if (CurrentAnchors.TryGetValue(row, out var anchors)) {
            for (int i = anchors.Count - 1; i >= 0; i = Math.Min(i - 1, anchors.Count - 1)) {
                var anchor = anchors[i];

                // Invalidate when range partially intersects
                if (startCol < anchor.MinCol && endCol > anchor.MinCol ||
                    startCol < anchor.MaxCol && endCol > anchor.MaxCol ||
                    // Remove entirely when it's 0 wide
                    anchor.MinCol == anchor.MaxCol && startCol <= anchor.MinCol && endCol >= anchor.MaxCol)
                {
                    anchor.OnRemoved?.Invoke();
                    anchors.Remove(anchor);
                }

                if (endCol <= anchor.MinCol) {
                    anchor.MinCol -= endCol - startCol;
                }
                if (endCol <= anchor.MaxCol) {
                    anchor.MaxCol -= endCol - startCol;
                }
            }
        }

        patch.Modify(row, CurrentLines[row].Remove(startCol, endCol - startCol));
    }

    /// Removes the specified line
    public void RemoveLine(int row) {
        using var patch = new Patch(this);
        patch.Delete(row);
    }

    /// Removes an inclusive range of lines from min..max
    public void RemoveLines(int min, int max) {
        using var patch = new Patch(this);
        patch.DeleteRange(min, max);
    }

    /// Replaces the inclusive range startCol..endCol with text inside the line
    public void ReplaceRangeInLine(int row, int startCol, int endCol, string text) {
        Debug.Assert(!text.Contains('\n') && !text.Contains('\r'));

        using var patch = new Patch(this);

        if (startCol > endCol) {
            (endCol, startCol) = (startCol, endCol);
        }

        // Update anchors
        if (CurrentAnchors.TryGetValue(row, out var anchors)) {
            for (int i = anchors.Count - 1; i >= 0; i = Math.Min(i - 1, anchors.Count - 1)) {
                var anchor = anchors[i];

                // Invalidate when range partially intersects
                if (startCol < anchor.MinCol && endCol > anchor.MinCol ||
                    startCol < anchor.MaxCol && endCol > anchor.MaxCol)
                {
                    anchor.OnRemoved?.Invoke();
                    anchors.Remove(anchor);
                }

                if (anchor.MinCol == anchor.MaxCol) {
                    // Paste the new text into the anchor
                    anchor.MaxCol += text.Length;
                } else {
                    if (endCol <= anchor.MinCol) {
                        anchor.MinCol -= endCol - startCol;
                        anchor.MinCol += text.Length;
                    }
                    if (endCol <= anchor.MaxCol) {
                        anchor.MaxCol -= endCol - startCol;
                        anchor.MaxCol += text.Length;
                    }
                }
            }
        }

        patch.Modify(row, CurrentLines[row].ReplaceRange(startCol, endCol - startCol, text));
    }

    public string GetSelectedText() => GetTextInRange(Selection.Start, Selection.End);
    public string GetTextInRange(CaretPosition start, CaretPosition end) {
        if (start > end) {
            (end, start) = (start, end);
        }

        if (start.Row == end.Row) {
            return CurrentLines[start.Row][start.Col..end.Col];
        }

        string[] lines = new string[end.Row - start.Row + 1];
        lines[0] = CurrentLines[start.Row][start.Col..];
        for (int i = 1, row = start.Row + 1; row < end.Row; i++, row++)
            lines[i] = CurrentLines[row];
        lines[^1] = CurrentLines[end.Row][..end.Col];

        return string.Join(NewLine, lines);
    }

    #endregion
}