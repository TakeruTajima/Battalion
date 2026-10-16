using System.IO;
using System.Text;

namespace Battalion.Sim
{
    /// <summary>イベントログの出力先。1イベント＝JSON 1行。</summary>
    public interface IEventSink
    {
        /// <summary>false ならシミュレーションはログ行を組み立てない（バッチ実行の高速化）。</summary>
        bool Enabled { get; }
        void Write(string jsonLine);
    }

    public sealed class NullEventSink : IEventSink
    {
        public static readonly NullEventSink Instance = new NullEventSink();
        public bool Enabled => false;
        public void Write(string jsonLine) { }
    }

    /// <summary>メモリに JSON Lines を貯める。改行は常に "\n"。</summary>
    public sealed class StringEventSink : IEventSink
    {
        readonly StringBuilder _sb = new StringBuilder();
        public bool Enabled => true;
        public int Count { get; private set; }

        public void Write(string jsonLine)
        {
            _sb.Append(jsonLine).Append('\n');
            Count++;
        }

        public override string ToString() => _sb.ToString();
        public byte[] ToUtf8() => new UTF8Encoding(false).GetBytes(_sb.ToString());
    }

    /// <summary>ファイル（UTF-8、BOM なし）に JSON Lines を書く。</summary>
    public sealed class FileEventSink : IEventSink, System.IDisposable
    {
        readonly StreamWriter _w;

        public FileEventSink(string path)
        {
            _w = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
        }

        public bool Enabled => true;
        public void Write(string jsonLine) => _w.WriteLine(jsonLine);
        public void Dispose() => _w.Dispose();
    }
}
