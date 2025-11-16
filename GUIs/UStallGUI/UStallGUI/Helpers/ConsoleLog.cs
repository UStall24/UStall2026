using System.Collections.Generic;
using System;

namespace UStallGUI.Helpers
{
    public class ConsoleLog
    {
        private readonly Queue<string> recentMessages = new();
        private readonly List<string> fullHistory = new();
        private int counter = 1;

        public string CurrentText { get; private set; }

        public void Add(string message)
        {
            string entry = $"[{counter++}] - {message}";
            recentMessages.Enqueue(entry);
            fullHistory.Add(entry);

            if (recentMessages.Count > 10)
                recentMessages.Dequeue();

            CurrentText = string.Join(Environment.NewLine, recentMessages);
        }

        public IEnumerable<string> GetFullHistory() => fullHistory;
    }

}
