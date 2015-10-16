using UnityEngine;
using System.Collections;
using System;
using System.Collections.Generic;

public class gstringTest : MonoBehaviour
{
    Dictionary<string, int> dict = new Dictionary<string, int>();

    ProfilerBlock profiler = ProfilerBlock.Instance;

    public string outsideString;
    public string outsideString1;

    void Update()
    {
        using (profiler.Sample("gstring"))
        {
            using (gstring.Block())
            {
                using (profiler.Sample("Format"))
                {
                    gstring gf = gstring.Format("Number = {0}, Float = {1} String = {2}", 123, 3.148f, "Text");
                    int x = 10; // declare an int, just so we can step over to it and inspect values when debugging
                }

                using (profiler.Sample("Concat"))
                {
                    gstring it = gstring.Concat("That's ", "a lot", " of", " strings", " to ", "concat");
                    int x = 10;
                }

                using (profiler.Sample("Substring + IndexOf + LastIndexOf"))
                {
                    gstring path = "Path/To/Some/File.txt";
                    int period = path.IndexOf('.');
                    var ext = path.Substring(period + 1);
                    var file = path.Substring(path.LastIndexOf('/') + 1, 4);
                    int x = 10;
                }

                using (profiler.Sample("Replace (char)"))
                {
                    gstring input = "This is some sort of text";
                    gstring replacement = input.Replace('o', '0').Replace('i', '1');
                    int x = 10;
                }

                using (profiler.Sample("Replace (string)"))
                {
                    gstring input = "m_This is the is is form of text";
                    gstring replacement = input.Replace("m_", "").Replace("is", "si");
                    int x = 10;
                }

                using (profiler.Sample("Concat + Intern"))
                {
                    for (int i = 0; i < 4; i++)
                        dict[gstring.Concat("Item", i).Intern()] = i;
                    outsideString1 = gstring.Concat("I'm ", "long ", "gone ", "by ", "the ", "end ", "of ", "this ", "gstring block");
                    outsideString = gstring.Concat("I'll ", "be ", "still ", "around ", "here").Intern();
                    int x = 10;
                }

                using (profiler.Sample("ToUpper + ToLower"))
                {
                    gstring s1 = "Hello";
                    gstring s2 = s1.ToUpper();
                    gstring s3 = s2 + s1.ToLower();
                    int x = 10;
                }
            }
        }

        using (profiler.Sample("string"))
        {
            using (profiler.Sample("Format"))
            {
                string gf = string.Format("Number = {0}, Float = {1} String = {2}", 123, 3.148f, "Text");
                int x = 10;
            }

            using (profiler.Sample("Concat"))
            {
                string it = string.Concat("That's ", "a lot ", " of", " strings", " to ", "concat");
                int x = 10;
            }

            using (profiler.Sample("Substring + IndexOf + LastIndexOf"))
            {
                string path = "Path/To/Some/File.txt";
                int period = path.IndexOf('.');
                var ext = path.Substring(period + 1);
                var file = path.Substring(path.LastIndexOf('/') + 1, 4);
                int x = 10;
            }

            using (profiler.Sample("Replace (char)"))
            {
                string input = "This is some sort of text";
                string replacement = input.Replace('o', 'O').Replace('i', 'I');
                int x = 10;
            }

            using (profiler.Sample("Replace (string)"))
            {
                string input = "m_This is the is is form of text";
                string replacement = input.Replace("m_", "").Replace("is", "si");
                int x = 10;
            }

            using (profiler.Sample("ToUpper + ToLower"))
            {
                string s1 = "Hello";
                string s2 = s1.ToUpper();
                string s3 = s2 + s1.ToLower();
                int x = 10;
            }

        }
    }

    public class ProfilerBlock : IDisposable
    {
        public static readonly ProfilerBlock Instance = new ProfilerBlock();

        public IDisposable Sample(string sample)
        {
            Profiler.BeginSample(sample);
            return this;
        }

        public void Dispose()
        {
            Profiler.EndSample();
        }
    }

}
