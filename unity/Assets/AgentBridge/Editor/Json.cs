// MiniJSON — compact, dependency-free JSON parser/serializer.
// Adapted from the public-domain MiniJSON by Calvin Rien (based on Patrick van Bergen's work).
// Parses into Dictionary<string, object> / List<object> / string / double / long / bool / null.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AgentBridge
{
    public static class Json
    {
        public static object Deserialize(string json)
        {
            if (json == null) return null;
            return Parser.Parse(json);
        }

        public static string Serialize(object obj)
        {
            return Serializer.Serialize(obj);
        }

        sealed class Parser : IDisposable
        {
            const string WordBreak = "{}[],:\"";
            System.IO.StringReader json;

            Parser(string jsonString) { json = new System.IO.StringReader(jsonString); }

            public static object Parse(string jsonString)
            {
                using (var instance = new Parser(jsonString)) return instance.ParseValue();
            }

            public void Dispose() { json.Dispose(); json = null; }

            Dictionary<string, object> ParseObject()
            {
                var table = new Dictionary<string, object>();
                json.Read(); // {
                while (true)
                {
                    switch (NextToken)
                    {
                        case TOKEN.NONE: return null;
                        case TOKEN.COMMA: continue;
                        case TOKEN.CURLY_CLOSE: return table;
                        default:
                            string name = ParseString();
                            if (name == null) return null;
                            if (NextToken != TOKEN.COLON) return null;
                            json.Read(); // :
                            table[name] = ParseValue();
                            break;
                    }
                }
            }

            List<object> ParseArray()
            {
                var array = new List<object>();
                json.Read(); // [
                bool parsing = true;
                while (parsing)
                {
                    TOKEN nextToken = NextToken;
                    switch (nextToken)
                    {
                        case TOKEN.NONE: return null;
                        case TOKEN.COMMA: continue;
                        case TOKEN.SQUARED_CLOSE: parsing = false; break;
                        default:
                            array.Add(ParseByToken(nextToken));
                            break;
                    }
                }
                return array;
            }

            object ParseValue() { return ParseByToken(NextToken); }

            object ParseByToken(TOKEN token)
            {
                switch (token)
                {
                    case TOKEN.STRING: return ParseString();
                    case TOKEN.NUMBER: return ParseNumber();
                    case TOKEN.CURLY_OPEN: return ParseObject();
                    case TOKEN.SQUARED_OPEN: return ParseArray();
                    case TOKEN.TRUE: return true;
                    case TOKEN.FALSE: return false;
                    case TOKEN.NULL: return null;
                    default: return null;
                }
            }

            string ParseString()
            {
                var s = new StringBuilder();
                json.Read(); // "
                bool parsing = true;
                while (parsing)
                {
                    if (json.Peek() == -1) break;
                    char c = NextChar;
                    switch (c)
                    {
                        case '"': parsing = false; break;
                        case '\\':
                            if (json.Peek() == -1) { parsing = false; break; }
                            c = NextChar;
                            switch (c)
                            {
                                case '"': s.Append('"'); break;
                                case '\\': s.Append('\\'); break;
                                case '/': s.Append('/'); break;
                                case 'b': s.Append('\b'); break;
                                case 'f': s.Append('\f'); break;
                                case 'n': s.Append('\n'); break;
                                case 'r': s.Append('\r'); break;
                                case 't': s.Append('\t'); break;
                                case 'u':
                                    var hex = new char[4];
                                    for (int i = 0; i < 4; i++) hex[i] = NextChar;
                                    s.Append((char)Convert.ToInt32(new string(hex), 16));
                                    break;
                            }
                            break;
                        default: s.Append(c); break;
                    }
                }
                return s.ToString();
            }

            object ParseNumber()
            {
                string number = NextWord;
                if (number.IndexOf('.') == -1 && number.IndexOf('e') == -1 && number.IndexOf('E') == -1)
                {
                    long parsedInt;
                    long.TryParse(number, NumberStyles.Any, CultureInfo.InvariantCulture, out parsedInt);
                    return parsedInt;
                }
                double parsedDouble;
                double.TryParse(number, NumberStyles.Any, CultureInfo.InvariantCulture, out parsedDouble);
                return parsedDouble;
            }

            void EatWhitespace()
            {
                while (char.IsWhiteSpace(PeekChar))
                {
                    json.Read();
                    if (json.Peek() == -1) break;
                }
            }

            char PeekChar => Convert.ToChar(json.Peek());
            char NextChar => Convert.ToChar(json.Read());

            string NextWord
            {
                get
                {
                    var word = new StringBuilder();
                    while (!IsWordBreak(PeekChar))
                    {
                        word.Append(NextChar);
                        if (json.Peek() == -1) break;
                    }
                    return word.ToString();
                }
            }

            static bool IsWordBreak(char c) => char.IsWhiteSpace(c) || WordBreak.IndexOf(c) != -1;

            TOKEN NextToken
            {
                get
                {
                    EatWhitespace();
                    if (json.Peek() == -1) return TOKEN.NONE;
                    switch (PeekChar)
                    {
                        case '{': return TOKEN.CURLY_OPEN;
                        case '}': json.Read(); return TOKEN.CURLY_CLOSE;
                        case '[': return TOKEN.SQUARED_OPEN;
                        case ']': json.Read(); return TOKEN.SQUARED_CLOSE;
                        case ',': json.Read(); return TOKEN.COMMA;
                        case '"': return TOKEN.STRING;
                        case ':': return TOKEN.COLON;
                        case '0': case '1': case '2': case '3': case '4':
                        case '5': case '6': case '7': case '8': case '9':
                        case '-': return TOKEN.NUMBER;
                    }
                    switch (NextWord)
                    {
                        case "false": return TOKEN.FALSE;
                        case "true": return TOKEN.TRUE;
                        case "null": return TOKEN.NULL;
                    }
                    return TOKEN.NONE;
                }
            }

            enum TOKEN { NONE, CURLY_OPEN, CURLY_CLOSE, SQUARED_OPEN, SQUARED_CLOSE, COLON, COMMA, STRING, NUMBER, TRUE, FALSE, NULL }
        }

        sealed class Serializer
        {
            readonly StringBuilder builder = new StringBuilder();
            Serializer() { }

            public static string Serialize(object obj)
            {
                var instance = new Serializer();
                instance.SerializeValue(obj);
                return instance.builder.ToString();
            }

            void SerializeValue(object value)
            {
                if (value == null) { builder.Append("null"); return; }
                if (value is string s) { SerializeString(s); return; }
                if (value is bool b) { builder.Append(b ? "true" : "false"); return; }
                if (value is IDictionary dict) { SerializeObject(dict); return; }
                if (value is IList list) { SerializeArray(list); return; }
                if (value is char c) { SerializeString(c.ToString()); return; }
                if (value is float f) { builder.Append(f.ToString("R", CultureInfo.InvariantCulture)); return; }
                if (value is double d) { builder.Append(d.ToString("R", CultureInfo.InvariantCulture)); return; }
                if (value is int || value is long || value is short || value is byte ||
                    value is uint || value is ulong || value is ushort || value is sbyte)
                {
                    builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                }
                if (value is IConvertible conv)
                {
                    builder.Append(conv.ToString(CultureInfo.InvariantCulture));
                    return;
                }
                SerializeString(value.ToString());
            }

            void SerializeObject(IDictionary obj)
            {
                bool first = true;
                builder.Append('{');
                foreach (object e in obj.Keys)
                {
                    if (!first) builder.Append(',');
                    SerializeString(e.ToString());
                    builder.Append(':');
                    SerializeValue(obj[e]);
                    first = false;
                }
                builder.Append('}');
            }

            void SerializeArray(IList array)
            {
                builder.Append('[');
                bool first = true;
                foreach (object obj in array)
                {
                    if (!first) builder.Append(',');
                    SerializeValue(obj);
                    first = false;
                }
                builder.Append(']');
            }

            void SerializeString(string str)
            {
                builder.Append('"');
                foreach (char c in str)
                {
                    switch (c)
                    {
                        case '"': builder.Append("\\\""); break;
                        case '\\': builder.Append("\\\\"); break;
                        case '\b': builder.Append("\\b"); break;
                        case '\f': builder.Append("\\f"); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;
                        default:
                            if (c < ' ') builder.AppendFormat("\\u{0:x4}", (int)c);
                            else builder.Append(c);
                            break;
                    }
                }
                builder.Append('"');
            }
        }
    }
}
