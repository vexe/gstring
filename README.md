# gstring
GC Free string for C#

Lets you do pretty much most of the common string operations without any alloactions.

See the video in this thread to see how I accomplished it http://forum.unity3d.com/threads/gstring-gc-free-string-for-unity.338588/

See gstringTest for sample usage code.

Note that the implementaiton uses pointers and "unsafe" code so make sure if you're on Unity to have the smcs and gmcs files with the -unsafe flag, you might have to restart Unity if it's the first time you add them.
