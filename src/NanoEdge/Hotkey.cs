using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace NanoEdge
{
    // 密封标签切换键。持久化格式 "Ctrl+Shift+S"：最后一段是 WPF Key 枚举名，
    // 前面是修饰键。不用 KeyGesture 字符串，是因为 Alt 组合在 WPF 里走 Key.System、
    // 在消息泵里走 WM_SYSKEYDOWN，自己解析才能同时喂给这两条输入路径。
    public static class Hotkey
    {
        public const string Default = "Alt+OemTilde";
        public const ModifierKeys Supported = ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt;

        private struct Chord
        {
            public ModifierKeys Mods;
            public Key Key;
            public bool Valid;
        }

        private static readonly Chord[] Reserved = BuildReserved();

        private static Chord C(ModifierKeys mods, Key key)
        {
            return new Chord { Mods = mods, Key = key, Valid = true };
        }

        // 读配置时容错：手改坏 settings.json 不能让切换键彻底消失。
        public static string Normalize(string spec)
        {
            var chord = Parse(spec);
            return chord.Valid ? Format(chord.Mods, chord.Key) : Default;
        }

        public static bool Matches(string spec, Key key, ModifierKeys modifiers)
        {
            var chord = Parse(spec);
            return chord.Valid &&
                   chord.Mods == (modifiers & Supported) &&
                   chord.Key == key;
        }

        public static string Display(string spec)
        {
            var chord = Parse(spec);
            if (!chord.Valid) chord = Parse(Default);
            var text = "";
            if ((chord.Mods & ModifierKeys.Control) != 0) text += "Ctrl+";
            if ((chord.Mods & ModifierKeys.Shift) != 0) text += "Shift+";
            if ((chord.Mods & ModifierKeys.Alt) != 0) text += "Alt+";
            return text + KeyLabel(chord.Key);
        }

        // WPF 在 Alt 组合下把 e.Key 置为 Key.System，真实按键在 e.SystemKey。
        // 消息泵传的是 Key.None 作为 systemKey，此时 e.Key 已经是真实按键。
        public static Key NormalizeKey(Key key, Key systemKey)
        {
            return key == Key.System && systemKey != Key.None ? systemKey : key;
        }

        public static bool IsModifierKey(Key key)
        {
            return key == Key.None || key == Key.System ||
                   key == Key.LeftCtrl || key == Key.RightCtrl ||
                   key == Key.LeftShift || key == Key.RightShift ||
                   key == Key.LeftAlt || key == Key.RightAlt ||
                   key == Key.LWin || key == Key.RWin;
        }

        // 录入用：返回可持久化的组合，null 表示不合法（裸键 / 纯修饰键 / 已被占用）。
        public static string Capture(Key key, ModifierKeys modifiers, Key systemKey)
        {
            var real = NormalizeKey(key, systemKey);
            var mods = modifiers & Supported;
            if (mods == ModifierKeys.None || IsModifierKey(real)) return null;
            if (IsReserved(mods, real)) return null;
            return Format(mods, real);
        }

        public static bool IsReserved(ModifierKeys mods, Key key)
        {
            mods &= Supported;
            foreach (var chord in Reserved)
                if (chord.Mods == mods && chord.Key == key) return true;
            return false;
        }

        private static Chord Parse(string spec)
        {
            var result = new Chord();
            if (string.IsNullOrWhiteSpace(spec)) return result;
            var parts = spec.Split('+');
            if (parts.Length < 2) return result;

            var mods = ModifierKeys.None;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].Trim().ToLowerInvariant())
                {
                    case "ctrl":
                    case "control": mods |= ModifierKeys.Control; break;
                    case "shift": mods |= ModifierKeys.Shift; break;
                    case "alt": mods |= ModifierKeys.Alt; break;
                    default: return result;
                }
            }

            Key key;
            if (!Enum.TryParse<Key>(parts[parts.Length - 1].Trim(), true, out key)) return result;
            if (IsModifierKey(key)) return result;

            result.Mods = mods;
            result.Key = key;
            result.Valid = true;
            return result;
        }

        private static string Format(ModifierKeys mods, Key key)
        {
            var text = "";
            if ((mods & ModifierKeys.Control) != 0) text += "Ctrl+";
            if ((mods & ModifierKeys.Shift) != 0) text += "Shift+";
            if ((mods & ModifierKeys.Alt) != 0) text += "Alt+";
            return text + key;
        }

        private static string KeyLabel(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9) return ((int)(key - Key.D0)).ToString();
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return "Num" + (int)(key - Key.NumPad0);
            if (key >= Key.F1 && key <= Key.F24) return key.ToString();

            switch (key)
            {
                case Key.OemTilde: return "`";
                case Key.OemPlus: return "+";
                case Key.OemMinus: return "-";
                case Key.OemQuestion: return "?";
                case Key.OemPeriod: return ".";
                case Key.OemComma: return ",";
                case Key.OemSemicolon: return ";";
                case Key.OemQuotes: return "'";
                case Key.OemOpenBrackets: return "[";
                case Key.OemCloseBrackets: return "]";
                case Key.OemBackslash: return "\\";
                case Key.OemPipe: return "\\";
                case Key.Left: return "←";
                case Key.Right: return "→";
                case Key.Up: return "↑";
                case Key.Down: return "↓";
                case Key.Space: return "Space";
                case Key.Tab: return "Tab";
                case Key.Enter: return "Enter";
                case Key.Back: return "Backspace";
                case Key.Escape: return "Esc";
                case Key.Add: return "Num+";
                case Key.Subtract: return "Num-";
            }

            var name = key.ToString();
            return name.Length == 1 ? name.ToUpperInvariant() : name;
        }

        // 宿主已占用的组合。让用户把切换键设成 Ctrl+T / F5 之类会静默顶掉原功能，
        // 录入时直接拒绝，不做事后优先级判断。
        private static Chord[] BuildReserved()
        {
            var ctrl = ModifierKeys.Control;
            var shift = ModifierKeys.Shift;
            var alt = ModifierKeys.Alt;
            var list = new List<Chord>
            {
                C(alt, Key.Left), C(alt, Key.Right), C(alt, Key.D),
                C(ctrl, Key.T), C(ctrl | shift, Key.T), C(ctrl, Key.W),
                C(ctrl, Key.D), C(ctrl, Key.H), C(ctrl | shift, Key.O), C(ctrl, Key.J),
                C(ctrl, Key.R), C(ctrl, Key.F5), C(ctrl, Key.F),
                C(ctrl, Key.D0), C(ctrl, Key.NumPad0),
                C(ctrl, Key.OemPlus), C(ctrl, Key.Add),
                C(ctrl, Key.OemMinus), C(ctrl, Key.Subtract),
                C(ctrl, Key.L), C(ctrl, Key.Tab), C(ctrl | shift, Key.Tab),
                C(ctrl | shift, Key.M), C(ctrl | shift, Key.L),
                C(ctrl, Key.F11), C(ctrl, Key.F12),
                C(ctrl, Key.N), C(ctrl, Key.P), C(ctrl, Key.S),
                C(ctrl, Key.A), C(ctrl, Key.C), C(ctrl, Key.V), C(ctrl, Key.X),
                C(ctrl, Key.Z), C(ctrl, Key.Y),
                C(ModifierKeys.None, Key.F5), C(ModifierKeys.None, Key.F11),
                C(ModifierKeys.None, Key.F12), C(ModifierKeys.None, Key.Escape)
            };
            for (var key = Key.D1; key <= Key.D9; key++) list.Add(C(ctrl, key));
            return list.ToArray();
        }
    }
}
