using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Y4NGZCompany.Contracts.Defuse
{
    internal sealed class DefuseTerminalSession
    {
        internal static readonly string[] Markings = { "KILO", "MANTA", "RUST", "OMEGA", "TYPE-7" };
        internal static readonly string[] Displays = { "ARMED", "ERROR", "LOCKED" };
        internal static readonly string[] Colors = { "RED", "BLUE", "YELLOW", "GREEN", "WHITE", "BLACK" };
        private enum Step { Home, Marking, Display, Count, Wire, Review }
        private Step _step;
        private string _marking, _display;
        private int _count;
        private readonly List<string> _wires = new List<string>();

        internal void Reset()
        {
            _step = Step.Home;
            _marking = _display = null;
            _count = 0;
            _wires.Clear();
        }

        internal bool TryHandle(string input, bool ownsPage, Func<string> status, Func<string> manual,
            Func<string> report, Func<string, string, string[], string> submit, out string text)
        {
            text = null;
            string command = (input ?? string.Empty).Trim().ToLowerInvariant();
            bool explicitCommand = command == "defuse" || command.StartsWith("defuse ", StringComparison.Ordinal)
                || command == "fieldsupport" || command.StartsWith("fieldsupport ", StringComparison.Ordinal)
                || command == "field support" || command.StartsWith("field support ", StringComparison.Ordinal);
            if (!ownsPage) Reset();
            if (explicitCommand)
            {
                int prefix = command.StartsWith("field support", StringComparison.Ordinal) ? 13 :
                    command.StartsWith("fieldsupport", StringComparison.Ordinal) ? 12 : 6;
                command = command.Substring(prefix).Trim();
            }
            else if (!ownsPage) return false;
            switch (command)
            {
                case "": case "home": case "cancel":
                    Reset(); text = Home(); return true;
                case "start": case "diagnostic": case "restart":
                    Reset(); _step = Step.Marking; text = Prompt(); return true;
                case "status": text = status(); return true;
                case "manual": case "instructions": text = manual(); return true;
                case "report": case "info": text = report(); return true;
                case "back":
                    Back(); text = _step == Step.Home ? Home() : Prompt(); return true;
                case "submit":
                    if (_step != Step.Review) { text = "Complete the observations before submitting.\n\n" + Prompt(); return true; }
                    text = submit(_marking, _display, _wires.ToArray());
                    if ((text ?? string.Empty).StartsWith("DIAGNOSTIC SAVED", StringComparison.OrdinalIgnoreCase))
                    { Reset(); text = report(); }
                    return true;
            }
            if (_step == Step.Home)
            {
                if (!explicitCommand) return false;
                text = Home(); return true;
            }
            string[] options = _step == Step.Marking ? Markings : _step == Step.Display ? Displays :
                _step == Step.Count ? new[] { "3", "4", "5" } : _step == Step.Wire ? Colors : Array.Empty<string>();
            int choice = Array.FindIndex(options, v => v.Equals(command, StringComparison.OrdinalIgnoreCase));
            if (choice < 0 && _step != Step.Count && int.TryParse(command, out int number)) choice = number - 1;
            if (choice < 0 || choice >= options.Length)
            {
                bool answer = explicitCommand || int.TryParse(command, out _)
                    || Markings.Concat(Displays).Concat(Colors).Any(v => v.Equals(command, StringComparison.OrdinalIgnoreCase));
                if (!answer) { Reset(); return false; } // Keep vanilla commands available during a form.
                text = "That is not one of the choices.\n\n" + Prompt(); return true;
            }
            if (_step == Step.Marking) { _marking = options[choice]; _step = Step.Display; }
            else if (_step == Step.Display) { _display = options[choice]; _step = Step.Count; }
            else if (_step == Step.Count) { _count = int.Parse(options[choice]); _wires.Clear(); _step = Step.Wire; }
            else if (_step == Step.Wire) { _wires.Add(options[choice]); if (_wires.Count == _count) _step = Step.Review; }
            text = Prompt(); return true;
        }

        private void Back()
        {
            if (_step == Step.Review) _step = Step.Wire;
            if (_step == Step.Wire)
            {
                if (_wires.Count > 0) _wires.RemoveAt(_wires.Count - 1);
                else _step = Step.Count;
            }
            else if (_step == Step.Count) _step = Step.Display;
            else if (_step == Step.Display) _step = Step.Marking;
            else Reset();
        }

        internal static string Home() =>
            "FIELD SUPPORT\n\nDEFUSE DIAGNOSTIC\nEnter the observations reported by the crew at the bomb.\n\n" +
            "START         Begin a diagnostic\nSTATUS        Current assignment\nINSTRUCTIONS  Defuse instructions\nREPORT        Cut procedure and briefcase code\n\n" +
            "Type DEFUSE to return here from any terminal page.\n";

        private string Prompt()
        {
            var sb = new StringBuilder("DEFUSE DIAGNOSTIC\n\n");
            string[] options;
            switch (_step)
            {
                case Step.Marking: sb.AppendLine("What marking is on the case?"); options = Markings; break;
                case Step.Display: sb.AppendLine("What does the bomb display say?"); options = Displays; break;
                case Step.Count:
                    sb.Append("How many wires are visible?\n\nType 3, 4, or 5.\n"); options = Array.Empty<string>(); break;
                case Step.Wire:
                    sb.AppendLine($"Wire {_wires.Count + 1} of {_count}, reading LEFT TO RIGHT:"); options = Colors; break;
                case Step.Review:
                    sb.AppendLine($"Marking: {_marking}\nDisplay: {_display}\nWires: {_count}");
                    sb.AppendLine("Left to right: " + string.Join(", ", _wires));
                    sb.AppendLine("Counts: " + string.Join(", ", Colors.Where(c => _wires.Contains(c)).Select(c => $"{c} {_wires.Count(w => w == c)}")));
                    sb.AppendLine("\nType SUBMIT to obtain the cut procedure and code."); options = Array.Empty<string>(); break;
                default: return Home();
            }
            for (int i = 0; i < options.Length; i++) sb.AppendLine($"{i + 1}. {options[i]}");
            sb.Append("\nBACK to correct an answer. CANCEL to leave the form.\n");
            return sb.ToString();
        }
    }
}
