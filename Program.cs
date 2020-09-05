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
                Console.WriteLine("Provide an ATX file name or file pattern to read. \"-verbose\" for more detail.");
                return;
            }

            string filespec = args[0];
            bool verbose = false;
            // Check for "-verbose"
            if (args.Length > 1)
            {
                if(args[0] == "-verbose")
                {
                    verbose = true;
                    filespec = args[1];
                } else if (args[1] == "-verbose")
                {
                    verbose = true;
                }
            }

            string fullpath = Path.GetFullPath(filespec);
            var filenames = Directory.EnumerateFiles(Path.GetDirectoryName(fullpath), Path.GetFileName(fullpath));

            foreach (string fname in filenames)
            {
                AtxDisk disk = new AtxDisk(verbose);
                disk.load_info(fname);
            }

        }
    }
}
