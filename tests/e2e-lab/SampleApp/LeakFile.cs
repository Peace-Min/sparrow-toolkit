using System.IO;

namespace SampleApp
{
    public class LeakFile
    {
        public byte[] ReadHead(string path)
        {
            var fs = new FileStream(path, FileMode.Open);
            var buf = new byte[16];
            fs.Read(buf, 0, buf.Length);
            fs.Close();
            return buf;
        }
    }
}
