using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

//#define DBG

// gstring (gcfreestring) is a string wrapper that uses pointers to mutate the string when performing misc string operations
// the purpose is to be able to perform most the common operations we do no strings (concat, format, replace, etc)
// without any allocation.
// gstring is not meant to be stored as member variables, but to quickly declare them in a 'gstring block', use them
// for whatever string operation you want, then dispose of them.
// The nice thing is that you don't have to manually dispose of gstrings, once you're in a block all assignments are
// registered so that when the block/scope ends all used gstrings are disposed.
//
// But what if you wanted to keep/store the result you calculated and not dispose of them?
// Well this is where 'intern' comes in - basically there's a runtime intern (cache) table
// of strings (similar to .NET's string const intern table).
// string str = result.Intern();
// Which basically says, if the string is in the intern (cache) table, return it
// otherwise allocate new memory for it and store it in the table, next time we ask for it, it's there.
// The nice thing about interning is that you could pre-intern your strings via the static method gstring.Intern
//
// NOTES:
// 1- The class is not designed with concurrency/threading in mind, it's meant to be used in Unity
// 2- Cultural stuff I did not consider as well
// 3- Again, you shouldn't have gstring members in your class. All gstring instances are meant to be disposed. 
//    You just quickly open up a gstring.Block() and use gstrings in it, if you want to store a result you get
//    back from a gstring operation use Intern

namespace System
{
    public class gstring
    {
        static Dictionary<int, Stack<gstring>> g_cache;
        static Stack<gstring_block>            g_blocks;
        static List<string>                    g_intern_table;
        static gstring_block                   g_current_block;
        static List<int>                       g_finds;
        static gstring[]                       g_format_args;

        const int  INITIAL_BLOCK_CAPACITY  = 32;
        const int  INITIAL_CACHE_CAPACITY  = 128;
        const int  INITIAL_STACK_CAPACITY  = 48;
        const int  INITIAL_INTERN_CAPACITY = 256;
        const char NEW_ALLOC_CHAR          = 'X';

        [NonSerialized] string _value;
        [NonSerialized] bool   _disposed;

        internal gstring()
        {
            throw new NotSupportedException();
        }

        internal gstring(int length)
        {
            _value = new string(NEW_ALLOC_CHAR, length);
        }

        static gstring()
        {
            Initialize(INITIAL_CACHE_CAPACITY,
                       INITIAL_STACK_CAPACITY,
                       INITIAL_BLOCK_CAPACITY,
                       INITIAL_INTERN_CAPACITY);

            g_finds = new List<int>(10);
            g_format_args = new gstring[10];
        }

        internal void dispose()
        {
            if (_disposed)
                throw new ObjectDisposedException(this);

            // At this point there *must* be a stack whose length is equal to ours
            // otherwise we wouldn't exist
            var stack = g_cache[Length];
            stack.Push(this);
#if DBG
            if (log != null)
                log("Disposed: " + _value + " Length=" + Length + " Stack=" + stack.Count);
#endif
            memcpy(_value, NEW_ALLOC_CHAR);

            _disposed = true;
        }

        internal static gstring get(string value)
        {
            if (value == null)
                return null;
#if DBG
            if (log != null)
                log("Getting: " + value);
#endif
            var result = get(value.Length);
            memcpy(dst: result, src: value);
            return result;
        }

        internal static string __intern(string value)
        {
            int idx = g_intern_table.IndexOf(value);
            if (idx != -1)
                return g_intern_table[idx];

            string interned = new string(NEW_ALLOC_CHAR, value.Length);
            memcpy(interned, value);
            g_intern_table.Add(interned);
#if DBG
            if (log != null)
                log("Interned: " + value);
#endif
            return interned;
        }

        internal static gstring get(int length)
        {
            if (g_current_block == null)
                throw new InvalidOperationException("Getting gstrings must be done in a gstring_block. Make sure you do a using(gstring.block())");

            if (length <= 0)
                throw new InvalidOperationException("Invalid length: " + length);

            gstring result;
            Stack<gstring> stack;
            if (!g_cache.TryGetValue(length, out stack))
            {
                stack = new Stack<gstring>(INITIAL_STACK_CAPACITY);
                for (int i = 0; i < INITIAL_STACK_CAPACITY; i++)
                    stack.Push(new gstring(length));
                g_cache[length] = stack;
                result = stack.Pop();
            }
            else
            {
                if (stack.Count == 0)
                {
                    if (Log != null)
                        Log("Stack=0 Allocating new gstring Length=" + length);
                    result = new gstring(length);
                }
                else
                {
                    result = stack.Pop();
#if DBG
                    if (log != null)
                        log("Popped Length=" + length + " Stack=" + stack.Count);
#endif
                }
            }

            result._disposed = false;

            g_current_block.push(result);

            return result;
        }

        internal static int get_digit_count(int value)
        {
            int cnt;
            for (cnt = 1; (value /= 10) > 0; cnt++);
            return cnt;
        }

        internal static int internal_index_of(string input, char value, int start)
        {
            return internal_index_of(input, value, start, input.Length - start);
        }

        internal static int internal_index_of(string input, string value)
        {
            return internal_index_of(input, value, 0, input.Length);
        }

        internal static int internal_index_of(string input, string value, int start)
        {
            return internal_index_of(input, value, start, input.Length - start);
        }

        internal unsafe static gstring internal_format(string input, int num_args)
        {
            // "{0} {1}", "Hello", "World" ->
            // "xxxxxxxxxxx"
            // "Helloxxxxxx"
            // "Hello xxxxx"
            // "Hello World"

            // "Player={0} Id={1}", "Jon", 10 ->
            // "xxxxxxxxxxxxxxxx"
            // "Player=xxxxxxxxx"
            // "Player=Jonxxxxxx"
            // "Player=Jon Id=xx"
            // "Player=Jon Id=10"

            if (input == null)
                throw new ArgumentNullException("value");

            int new_len = input.Length - 3 * num_args;

            for (int i = 0; i < num_args; i++)
            {
                gstring arg = g_format_args[i];
                new_len += arg.Length;
            }

            gstring result = get(new_len);
            string res_value = result._value;

            int brace_idx = -3;
            for(int i = 0, j = 0, x = 0; x < num_args; x++)
            {
                string arg = g_format_args[x]._value;
                brace_idx = internal_index_of(input, '{', brace_idx + 3);
                if (brace_idx == -1)
                    throw new InvalidOperationException("Couldn't find open brace for argument " + arg);
                if (brace_idx + 2 >= input.Length || input[brace_idx + 2] != '}')
                    throw new InvalidOperationException("Couldn't find close brace for argument " + arg);

                fixed(char* ptr_input = input)
                {
                    fixed(char* ptr_result = res_value)
                    {
                        for(int k = 0; i < new_len; )
                        {
                            if (j < brace_idx)
                                ptr_result[i++] = ptr_input[j++];
                            else
                            {
                                ptr_result[i++] = arg[k++];
                                if (k == arg.Length)
                                {
                                    j += 3;
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            return result;
        }

        internal unsafe static int internal_index_of(string input, char value, int start, int count)
        {
            if (start < 0 || start >= input.Length)
                throw new ArgumentOutOfRangeException("start");

            if (start + count > input.Length)
                throw new ArgumentOutOfRangeException("count=" + count + " start+count=" + start + count);

            fixed (char* ptr_this = input)
            {
                int end = start + count;
                for(int i = start; i < end; i++)
                    if (ptr_this[i] == value)
                        return i;
                return -1;
            }
        }

        internal unsafe static int internal_index_of(string input, string value, int start, int count)
        {
            int input_len = input.Length;

            if (start < 0 || start >= input_len)
                throw new ArgumentOutOfRangeException("start");

            if (count < 0 || start + count > input_len)
                throw new ArgumentOutOfRangeException("count=" + count + " start+count=" + (start + count));

            if (count == 0)
                return -1;

            fixed (char* ptr_input = input)
            {
                fixed (char* ptr_value = value)
                {
                    int found = 0;
                    int end = start + count;
                    for(int i = start; i < end; i++)
                    {
                        for(int j = 0; j < value.Length && i + j < input_len; j++)
                        {
                            if (ptr_input[i + j] == ptr_value[j])
                            {
                                found++;
                                if (found == value.Length)
                                    return i;
                                continue;
                            }
                            if (found > 0)
                                break;
                        }
                    }
                    return -1;
                }
            }
        }

        internal unsafe static gstring internal_remove(string input, int start, int count)
        {
            if (start < 0 || start >= input.Length)
                throw new ArgumentOutOfRangeException("start=" + start + " Length=" + input.Length);

            if (count < 0 || start + count > input.Length)
                throw new ArgumentOutOfRangeException("count=" + count + " start+count=" + (start + count) + " Length=" + input.Length);

            if (count == 0)
                return input;

            gstring result = get(input.Length - count);
            internal_remove(result, input, start, count);
            return result;
        }

        internal unsafe static void internal_remove(string dst, string src, int start, int count)
        {
            fixed(char* src_ptr = src)
            {
                fixed(char* dst_ptr = dst)
                {
                    for(int i = 0, j = 0; i < dst.Length; i++)
                    {
                        if (i >= start && i < start + count) // within removal range
                            continue;
                        dst_ptr[j++] = src_ptr[i];
                    }
                }
            }
        }

        internal unsafe static gstring internal_replace(string value, string old_value, string new_value)
        {
            // "Hello, World. There World" | World->Jon =
            // "000000000000000000000" (len = orig - 2 * (world-jon) = orig - 4
            // "Hello, 00000000000000"
            // "Hello, Jon00000000000"
            // "Hello, Jon. There 000"
            // "Hello, Jon. There Jon"

            // "Hello, World. There World" | World->Alexander =
            // "000000000000000000000000000000000" (len = orig + 2 * (alexander-world) = orig + 8
            // "Hello, 00000000000000000000000000"
            // "Hello, Alexander00000000000000000"
            // "Hello, Alexander. There 000000000"
            // "Hello, Alexander. There Alexander"

            if (old_value == null)
                throw new ArgumentNullException("old_value");

            if (new_value == null)
                throw new ArgumentNullException("new_value");

            int idx = internal_index_of(value, old_value);
            if (idx == -1)
                return value;

            g_finds.Clear();
            g_finds.Add(idx);

            // find all the indicies beforehand
            while(idx + old_value.Length < value.Length)
            {
                idx = internal_index_of(value, old_value, idx + old_value.Length);
                if (idx == -1)
                    break;
                g_finds.Add(idx);
            }

            // calc the right new total length
            int new_len;
            int dif = old_value.Length - new_value.Length;
            if (dif > 0)
                new_len = value.Length - (g_finds.Count * dif);
            else
                new_len = value.Length + (g_finds.Count * -dif);

            gstring result = get(new_len);
            fixed(char* ptr_this = value)
            {
                fixed(char* ptr_result = result._value)
                {
                    for (int i = 0, x = 0, j = 0; i < new_len;)
                    {
                        if (x == g_finds.Count || g_finds[x] != j)
                        {
                            ptr_result[i++] = ptr_this[j++];
                        }
                        else
                        {
                            for (int n = 0; n < new_value.Length; n++)
                                ptr_result[i + n] = new_value[n];

                            x++;
                            i += new_value.Length;
                            j += old_value.Length;
                        }
                    }
                }
            }
            return result;
        }

        internal unsafe static gstring internal_insert(string value, char to_insert, int start, int count)
        {
            // "HelloWorld" (to_insert=x, start=5, count=3) -> "HelloxxxWorld"

            if (start < 0 || start >= value.Length)
                throw new ArgumentOutOfRangeException("start=" + start + " Length=" + value.Length);

            if (count < 0)
                throw new ArgumentOutOfRangeException("count=" + count);

            if (count == 0)
                return get(value);

            int new_len = value.Length + count;
            gstring result = get(new_len);
            fixed(char* ptr_value = value)
            {
                fixed(char* ptr_result = result._value)
                {
                    for(int i = 0, j = 0; i < new_len; i++)
                    {
                        if (i >= start && i < start + count)
                            ptr_result[i] = to_insert;
                        else
                            ptr_result[i] = ptr_value[j++];
                    }
                }
            }
            return result;
        }

        internal unsafe static gstring internal_insert(string input, string to_insert, int start)
        {
            if (input == null)
                throw new ArgumentNullException("input");

            if (to_insert == null)
                throw new ArgumentNullException("to_insert");

            if (start < 0 || start >= input.Length)
                throw new ArgumentOutOfRangeException("start=" + start + " Length=" + input.Length);

            if (to_insert.Length == 0)
                return get(input);

            int new_len = input.Length + to_insert.Length;
            gstring result = get(new_len);
            internal_insert(result, input, to_insert, start);
            return result;
        }

        internal unsafe static gstring internal_concat(string s1, string s2)
        {
            int total_length = s1.Length + s2.Length;
            gstring result = get(total_length);
            fixed(char* ptr_result = result._value)
            {
                fixed(char* ptr_s1 = s1)
                {
                    fixed(char* ptr_s2 = s2)
                    {
                        memcpy(dst: ptr_result, src: ptr_s1, length: s1.Length, src_offset: 0);
                        memcpy(dst: ptr_result, src: ptr_s2, length: s2.Length, src_offset: s1.Length);
                    }
                }
            }
            return result;
        }

        internal unsafe static void internal_insert(string dst, string src, string to_insert, int start)
        {
            fixed(char* ptr_src = src)
            {
                fixed(char* ptr_dst = dst)
                {
                    fixed(char* ptr_to_insert = to_insert)
                    {
                        for(int i = 0, j = 0, k = 0; i < dst.Length; i++)
                        {
                            if (i >= start && i < start + to_insert.Length)
                                ptr_dst[i] = ptr_to_insert[k++];
                            else
                                ptr_dst[i] = ptr_src[j++];
                        }
                    }
                }
            }
        }

        internal unsafe static void intcpy(char* dst, int value, int start, int count)
        {
            int end = start + count;
            for (int i = end - 1; i >= start; i--, value /= 10)
                *(dst + i) = (char)(value % 10 + 48);
        }

        internal unsafe static void memcpy(char* dst, char* src, int count)
        {
            for(int i = 0; i < count; i++)
                *(dst++) = *(src++);
        }

        internal unsafe static void memcpy(string dst, char src)
        {
            fixed (char* ptr_dst = dst)
            {
                int len = dst.Length;
                for (int i = 0; i < len; i++)
                    ptr_dst[i] = src;
            }
        }

        internal unsafe static void memcpy(string dst, char src, int index)
        {
            fixed (char* ptr = dst)
                ptr[index] = src;
        }

        internal unsafe static void memcpy(string dst, string src)
        {
            if (dst.Length != src.Length)
                throw new InvalidOperationException("Length mismatch");

            memcpy(dst, src, dst.Length, 0);
        }

        internal unsafe static void memcpy(char* dst, char* src, int length, int src_offset)
        {
            for (int i = 0; i < length; i++)
                *(dst + i + src_offset) = *(src + i);
        }

        internal unsafe static void memcpy(string dst, string src, int length, int src_offset)
        {
            fixed (char* ptr_dst = dst)
            {
                fixed (char* ptr_src = src)
                {
                    for (int i = 0; i < length; i++)
                        ptr_dst[i + src_offset] = ptr_src[i];
                }
            }
        }

        internal class gstring_block : IDisposable
        {
            readonly Stack<gstring> stack;

            internal gstring_block(int capacity)
            {
                stack = new Stack<gstring>(capacity);
            }

            internal void push(gstring str)
            {
                stack.Push(str);
            }

            internal IDisposable begin()
            {
#if DBG
                if (log != null)
                    log("Began block");
#endif
                return this;
            }

            void IDisposable.Dispose()
            {
#if DBG
                if (log != null)
                    log("Disposing block");
#endif
                while (stack.Count > 0)
                {
                    var str = stack.Pop();
                    str.dispose();
                }

                gstring.g_blocks.Push(this);
            }
        }

        // Public API
        #region

        public static Action<string> Log = null;

        public static int DecimalAccuracy = 3; // digits after the decimal point

        public int Length
        {
            get { return _value.Length; }
        }

        public static void Initialize(int cache_capacity, int stack_capacity, int block_capacity, int intern_capacity)
        {
            g_cache = new Dictionary<int, Stack<gstring>>(cache_capacity);
            g_blocks = new Stack<gstring_block>(block_capacity);
            g_intern_table = new List<string>(intern_capacity);

            for (int c = 1; c < cache_capacity; c++)
            {
                var stack = new Stack<gstring>(stack_capacity);
                for (int j = 0; j < stack_capacity; j++)
                    stack.Push(new gstring(c));
                g_cache[c] = stack;
            }

            for (int i = 0; i < block_capacity; i++)
            {
                var block = new gstring_block(block_capacity * 2);
                g_blocks.Push(block);
            }
        }

        public static IDisposable Block()
        {
            if (g_blocks.Count == 0)
                g_current_block = new gstring_block(INITIAL_BLOCK_CAPACITY * 2);
            else
                g_current_block = g_blocks.Pop();
            return g_current_block.begin();
        }

        public string Intern()
        {
            return __intern(_value);
        }

        public static string Intern(string value)
        {
            return __intern(value);
        }

        public static void Intern(string[] values)
        {
            for(int i = 0; i < values.Length; i++)
                __intern(values[i]);
        }

        public char this[int i]
        {
            get { return _value[i]; }
            set { memcpy(this, value, i); }
        }

        public override int GetHashCode()
        {
            return RuntimeHelpers.GetHashCode(_value);
        }

        public override bool Equals(object obj)
        {
            if (obj == null)
                return ReferenceEquals(this, null);

            var gstr = obj as gstring;
            if (gstr != null)
                return gstr._value == this._value;

            var str = obj as string;
            if (str != null)
                return str == this._value;

            return false;
        }

        public override string ToString()
        {
            return _value;
        }

        public static implicit operator gstring(bool value)
        {
            return get(value ? "True" : "False");
        }

        public unsafe static implicit operator gstring(int value)
        {
            // e.g. 125
            // first pass: count the number of digits
            // then: get a gstring with length = num digits
            // finally: iterate again, get the char of each digit, memcpy char to result
            bool negative = value < 0;
            value = Math.Abs(value);
            int num_digits = get_digit_count(value);
            gstring result;
            if (negative)
            {
                result = get(num_digits + 1);
                fixed(char* ptr = result._value)
                {
                    *ptr = '-';
                    intcpy(ptr, value, 1, num_digits);
                }
            }
            else
            {
                result = get(num_digits);
                fixed(char* ptr = result._value)
                    intcpy(ptr, value, 0, num_digits);
            }
            return result;
        }

        public unsafe static implicit operator gstring(float value)
        {
            // e.g. 3.148
            bool negative = value < 0;
            if (negative) value = -value;
            int mul = (int)Math.Pow(10, DecimalAccuracy);
            int number = (int)(value * mul); // gets the number as a whole, e.g. 3148
            int left_num = number / mul; // left part of the decimal point, e.g. 3
            int right_num = number % mul; // right part of the decimal pnt, e.g. 148
            int left_digit_count = get_digit_count(left_num); // e.g. 1
            int right_digit_count = get_digit_count(right_num); // e.g. 3
            int total = left_digit_count + right_digit_count + 1; // +1 for '.'

            gstring result;
            if (negative)
            {
                result = get(total + 1); // +1 for '-'
                fixed(char* ptr = result._value)
                {
                    *ptr = '-';
                    intcpy(ptr, left_num, 1, left_digit_count);
                    *(ptr + left_digit_count + 1) = '.';
                    intcpy(ptr, right_num, left_digit_count + 2, right_digit_count);
                }
            }
            else
            {
                result = get(total);
                fixed(char* ptr = result._value)
                {
                    intcpy(ptr, left_num, 0, left_digit_count);
                    *(ptr + left_digit_count) = '.';
                    intcpy(ptr, right_num, left_digit_count + 1, right_digit_count);
                }
            }
            return result;
        }

        public static implicit operator gstring(string value)
        {
            return get(value);
        }

        public static implicit operator string(gstring value)
        {
            return value._value;
        }

        public static gstring operator +(gstring left, gstring right)
        {
            return internal_concat(left, right);
        }

        public static bool operator ==(gstring left, gstring right)
        {
            if (ReferenceEquals(left, null))
                return ReferenceEquals(right, null);
            if (ReferenceEquals(right, null))
                return false;
            return left._value == right._value;
        }

        public static bool operator !=(gstring left, gstring right)
        {
            return !(left._value == right._value);
        }

        public unsafe gstring ToUpper()
        {
            var result = get(Length);
            fixed(char* ptr_this = this._value)
            {
                fixed(char* ptr_result = result._value)
                {
                    for(int i = 0; i < _value.Length; i++)
                    {
                        var ch = ptr_this[i];
                        if (char.IsLower(ch))
                            ptr_result[i] = char.ToUpper(ch);
                        else
                            ptr_result[i] = ptr_this[i];
                    }
                }
            }
            return result;
        }

        public unsafe gstring ToLower()
        {
            var result = get(Length);
            fixed(char* ptr_this = this._value)
            {
                fixed(char* ptr_result = result._value)
                {
                    for(int i = 0;i < _value.Length; i++)
                    {
                        var ch = ptr_this[i];
                        if (char.IsUpper(ch))
                            ptr_result[i] = char.ToLower(ch);
                        else
                            ptr_result[i] = ptr_this[i];
                    }
                }
            }
            return result;
        }

        public gstring Remove(int start)
        {
            return Remove(start, Length - start);
        }

        public gstring Remove(int start, int count)
        {
            return internal_remove(this._value, start, count);
        }

        public gstring Insert(char value, int start, int count)
        {
            return internal_insert(this._value, value, start, count);
        }

        public gstring Insert(string value, int start)
        {
            return internal_insert(this._value, value, start);
        }

        public unsafe gstring Replace(char old_value, char new_value)
        {
            gstring result = get(Length);
            fixed(char* ptr_this = this._value)
            {
                fixed(char* ptr_result = result._value)
                {
                    for(int i = 0; i < Length; i++)
                    {
                        if (ptr_this[i] == old_value)
                            ptr_result[i] = new_value;
                        else
                            ptr_result[i] = ptr_this[i];
                    }
                }
            }
            return result;
        }

        public gstring Replace(string old_value, string new_value)
        {
            return internal_replace(this._value, old_value, new_value);
        }

        public gstring Substring(int start)
        {
            return Substring(start, Length - start);
        }

        public unsafe gstring Substring(int start, int count)
        {
            if (start < 0 || start >= Length)
                throw new ArgumentOutOfRangeException("start");

            if (count > Length)
                throw new ArgumentOutOfRangeException("count");

            gstring result = get(count);
            fixed(char* src = this._value)
                fixed(char* dst = result._value)
                    memcpy(dst, src + start, count);

            return result;
        }

        public bool Contains(string value)
        {
            return IndexOf(value) != -1;
        }

        public bool Contains(char value)
        {
            return IndexOf(value) != -1;
        }

        public int LastIndexOf(string value)
        {
            int idx = -1;
            int last_find = -1;
            while(true)
            {
                idx = internal_index_of(this._value, value, idx + value.Length);
				last_find = idx;
                if (idx == -1 || idx + value.Length >= this._value.Length)
                    break;
            }
            return last_find;
        }

        public int LastIndexOf(char value)
        {
            int idx = -1;
            int last_find = -1;
            while(true)
            {
                idx = internal_index_of(this._value, value, idx + 1);
				last_find = idx;
                if (idx == -1 || idx + 1 >= this._value.Length)
                    break;
            }
            return last_find;
        }

        public int IndexOf(char value)
        {
            return IndexOf(value, 0, Length);
        }

        public int IndexOf(char value, int start)
        {
            return internal_index_of(this._value, value, start);
        }

        public int IndexOf(char value, int start, int count)
        {
            return internal_index_of(this._value, value, start, count);
        }

        public int IndexOf(string value)
        {
            return IndexOf(value, 0, Length);
        }

        public int IndexOf(string value, int start)
        {
            return IndexOf(value, start, Length - start);
        }

        public int IndexOf(string value, int start, int count)
        {
            return internal_index_of(this._value, value, start, count);
        }

        public unsafe bool EndsWith(string postfix)
        {
            if (postfix == null)
                throw new ArgumentNullException("postfix");

            if (this.Length < postfix.Length)
                return false;

            fixed(char* ptr_this = this._value)
            {
                fixed(char* ptr_postfix = postfix)
                {
                    for (int i = this._value.Length - 1, j = postfix.Length - 1; j >= 0; i--, j--)
                        if (ptr_this[i] != ptr_postfix[j])
                            return false;
                }
            }

            return true;
        }

        public unsafe bool StartsWith(string prefix)
        {
            if (prefix == null)
                throw new ArgumentNullException("prefix");

            if (this.Length < prefix.Length)
                return false;

            fixed(char* ptr_this = this._value)
            {
                fixed(char* ptr_prefix = prefix)
                {
                    for (int i = 0; i < prefix.Length; i++)
                        if (ptr_this[i] != ptr_prefix[i])
                            return false;
                }
            }

            return true;
        }

        public static int GetCacheCount(int length)
        {
            Stack<gstring> stack;
            if (!g_cache.TryGetValue(length, out stack))
                return -1;
            return stack.Count;
        }

        public gstring Concat(gstring value)
        {
            return internal_concat(this, value);
        }

        public static gstring Concat(gstring s0, gstring s1) { return s0 + s1; }

        public static gstring Concat(gstring s0, gstring s1, gstring s2) { return s0 + s1 + s2; }

        public static gstring Concat(gstring s0, gstring s1, gstring s2, gstring s3) { return s0 + s1 + s2 + s3; }

        public static gstring Concat(gstring s0, gstring s1, gstring s2, gstring s3, gstring s4) { return s0 + s1 + s2 + s3 + s4; }

        public static gstring Concat(gstring s0, gstring s1, gstring s2, gstring s3, gstring s4, gstring s5) { return s0 + s1 + s2 + s3 + s4 + s5; }

        public static gstring Concat(gstring s0, gstring s1, gstring s2, gstring s3, gstring s4, gstring s5, gstring s6) { return s0 + s1 + s2 + s3 + s4 + s5 + s6; }

        public static gstring Concat(gstring s0, gstring s1, gstring s2, gstring s3, gstring s4, gstring s5, gstring s6, gstring s7) { return s0 + s1 + s2 + s3 + s4 + s5 + s6 + s7; }

        public static gstring Concat(gstring s0, gstring s1, gstring s2, gstring s3, gstring s4, gstring s5, gstring s6, gstring s7, gstring s8) { return s0 + s1 + s2 + s3 + s4 + s5 + s6 + s7 + s8; }

        public static gstring Concat(gstring s0, gstring s1, gstring s2, gstring s3, gstring s4, gstring s5, gstring s6, gstring s7, gstring s8, gstring s9) { return s0 + s1 + s2 + s3 + s4 + s5 + s6 + s7 + s8 + s9; }

        public static gstring Format(string input, gstring arg0, gstring arg1, gstring arg2, gstring arg3, gstring arg4, gstring arg5, gstring arg6, gstring arg7, gstring arg8, gstring arg9)
        {
            if (arg0 == null) throw new ArgumentNullException("arg0");
            if (arg1 == null) throw new ArgumentNullException("arg1");
            if (arg2 == null) throw new ArgumentNullException("arg2");
            if (arg3 == null) throw new ArgumentNullException("arg3");
            if (arg4 == null) throw new ArgumentNullException("arg4");
            if (arg5 == null) throw new ArgumentNullException("arg5");
            if (arg6 == null) throw new ArgumentNullException("arg6");
            if (arg7 == null) throw new ArgumentNullException("arg7");
            if (arg8 == null) throw new ArgumentNullException("arg8");
            if (arg9 == null) throw new ArgumentNullException("arg9");

            g_format_args[0] = arg0;
            g_format_args[1] = arg1;
            g_format_args[2] = arg2;
            g_format_args[3] = arg3;
            g_format_args[4] = arg4;
            g_format_args[5] = arg5;
            g_format_args[6] = arg6;
            g_format_args[7] = arg7;
            g_format_args[8] = arg8;
            g_format_args[9] = arg9;
            return internal_format(input, 10);
        }

        public static gstring Format(string input, gstring arg0, gstring arg1, gstring arg2, gstring arg3, gstring arg4, gstring arg5, gstring arg6, gstring arg7, gstring arg8)
        {
            if (arg0 == null) throw new ArgumentNullException("arg0");
            if (arg1 == null) throw new ArgumentNullException("arg1");
            if (arg2 == null) throw new ArgumentNullException("arg2");
            if (arg3 == null) throw new ArgumentNullException("arg3");
            if (arg4 == null) throw new ArgumentNullException("arg4");
            if (arg5 == null) throw new ArgumentNullException("arg5");
            if (arg6 == null) throw new ArgumentNullException("arg6");
            if (arg7 == null) throw new ArgumentNullException("arg7");
            if (arg8 == null) throw new ArgumentNullException("arg8");

            g_format_args[0] = arg0;
            g_format_args[1] = arg1;
            g_format_args[2] = arg2;
            g_format_args[3] = arg3;
            g_format_args[4] = arg4;
            g_format_args[5] = arg5;
            g_format_args[6] = arg6;
            g_format_args[7] = arg7;
            g_format_args[8] = arg8;
            return internal_format(input, 9);
        }

        public static gstring Format(string input, gstring arg0, gstring arg1, gstring arg2, gstring arg3, gstring arg4, gstring arg5, gstring arg6, gstring arg7)
        {
            if (arg0 == null) throw new ArgumentNullException("arg0");
            if (arg1 == null) throw new ArgumentNullException("arg1");
            if (arg2 == null) throw new ArgumentNullException("arg2");
            if (arg3 == null) throw new ArgumentNullException("arg3");
            if (arg4 == null) throw new ArgumentNullException("arg4");
            if (arg5 == null) throw new ArgumentNullException("arg5");
            if (arg6 == null) throw new ArgumentNullException("arg6");
            if (arg7 == null) throw new ArgumentNullException("arg7");

            g_format_args[0] = arg0;
            g_format_args[1] = arg1;
            g_format_args[2] = arg2;
            g_format_args[3] = arg3;
            g_format_args[4] = arg4;
            g_format_args[5] = arg5;
            g_format_args[6] = arg6;
            g_format_args[7] = arg7;
            return internal_format(input, 8);
        }

        public static gstring Format(string input, gstring arg0, gstring arg1, gstring arg2, gstring arg3, gstring arg4, gstring arg5, gstring arg6)
        {
            if (arg0 == null) throw new ArgumentNullException("arg0");
            if (arg1 == null) throw new ArgumentNullException("arg1");
            if (arg2 == null) throw new ArgumentNullException("arg2");
            if (arg3 == null) throw new ArgumentNullException("arg3");
            if (arg4 == null) throw new ArgumentNullException("arg4");
            if (arg5 == null) throw new ArgumentNullException("arg5");
            if (arg6 == null) throw new ArgumentNullException("arg6");

            g_format_args[0] = arg0;
            g_format_args[1] = arg1;
            g_format_args[2] = arg2;
            g_format_args[3] = arg3;
            g_format_args[4] = arg4;
            g_format_args[5] = arg5;
            g_format_args[6] = arg6;
            return internal_format(input, 7);
        }

        public static gstring Format(string input, gstring arg0, gstring arg1, gstring arg2, gstring arg3, gstring arg4, gstring arg5)
        {
            if (arg0 == null) throw new ArgumentNullException("arg0");
            if (arg1 == null) throw new ArgumentNullException("arg1");
            if (arg2 == null) throw new ArgumentNullException("arg2");
            if (arg3 == null) throw new ArgumentNullException("arg3");
            if (arg4 == null) throw new ArgumentNullException("arg4");
            if (arg5 == null) throw new ArgumentNullException("arg5");

            g_format_args[0] = arg0;
            g_format_args[1] = arg1;
            g_format_args[2] = arg2;
            g_format_args[3] = arg3;
            g_format_args[4] = arg4;
            g_format_args[5] = arg5;
            return internal_format(input, 6);
        }

        public static gstring Format(string input, gstring arg0, gstring arg1, gstring arg2, gstring arg3, gstring arg4)
        {
            if (arg0 == null) throw new ArgumentNullException("arg0");
            if (arg1 == null) throw new ArgumentNullException("arg1");
            if (arg2 == null) throw new ArgumentNullException("arg2");
            if (arg3 == null) throw new ArgumentNullException("arg3");
            if (arg4 == null) throw new ArgumentNullException("arg4");

            g_format_args[0] = arg0;
            g_format_args[1] = arg1;
            g_format_args[2] = arg2;
            g_format_args[3] = arg3;
            g_format_args[4] = arg4;
            return internal_format(input, 5);
        }

        public static gstring Format(string input, gstring arg0, gstring arg1, gstring arg2, gstring arg3)
        {
            if (arg0 == null) throw new ArgumentNullException("arg0");
            if (arg1 == null) throw new ArgumentNullException("arg1");
            if (arg2 == null) throw new ArgumentNullException("arg2");
            if (arg3 == null) throw new ArgumentNullException("arg3");

            g_format_args[0] = arg0;
            g_format_args[1] = arg1;
            g_format_args[2] = arg2;
            g_format_args[3] = arg3;
            return internal_format(input, 4);
        }

        public static gstring Format(string input, gstring arg0, gstring arg1, gstring arg2)
        {
            if (arg0 == null) throw new ArgumentNullException("arg0");
            if (arg1 == null) throw new ArgumentNullException("arg1");
            if (arg2 == null) throw new ArgumentNullException("arg2");

            g_format_args[0] = arg0;
            g_format_args[1] = arg1;
            g_format_args[2] = arg2;
            return internal_format(input, 3);
        }

        public static gstring Format(string input, gstring arg0, gstring arg1)
        {
            if (arg0 == null) throw new ArgumentNullException("arg0");
            if (arg1 == null) throw new ArgumentNullException("arg1");

            g_format_args[0] = arg0;
            g_format_args[1] = arg1;
            return internal_format(input, 2);
        }

        public static gstring Format(string input, gstring arg0)
        {
            if (arg0 == null) throw new ArgumentNullException("arg0");

            g_format_args[0] = arg0;
            return internal_format(input, 1);
        }

        #endregion

    }

    public static class GStringExtensions
    {
        public static bool IsNullOrEmpty(this gstring str)
        {
            return str == null || str.Length == 0;
        }

        public static bool IsPrefix(this gstring str, string value)
        {
            return str.StartsWith(value);
        }

        public static bool ispostfix(this gstring str, string postfix)
        {
            return str.EndsWith(postfix);
        }
    }
}
