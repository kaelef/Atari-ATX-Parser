using System;
using System.IO;

namespace AtxInfo
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Provide an ATX file name or file pattern to read");
                return;
            }

            string fullpath = Path.GetFullPath(args[0]);
            var filenames = Directory.EnumerateFiles(Path.GetDirectoryName(fullpath), Path.GetFileName(fullpath));

            foreach (string fname in filenames)
            {
                AtxDisk disk = new AtxDisk();
                disk.load_info(fname);
            }

        }
    }
}
