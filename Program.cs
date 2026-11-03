using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using S7.Net;

// ⭐ 解决 DataType 命名冲突
using S7DataType = S7.Net.DataType;

namespace S7200SmartReader {
    class Program {
        // ===== 默认参数 =====
        private const string DefaultIp = "192.168.2.2";
        private const int DefaultStartByte = 1200;
        private const int DefaultReadLength = 40;
        private const int DefaultPollIntervalMs = 1000;
        private const int DbNumber = 1;   // S7-200 SMART 的 V 区固定映射为 DB1

        // ⭐ 全局唯一的 PLC IP，程序启动时输入一次
        private static string _plcIp = DefaultIp;

        static void Main() {
            Console.WriteLine("=== S7-200 SMART V区读写工具 ===");

            // ⭐ 只在启动时询问一次 IP
            _plcIp = PromptString($"PLC IP (默认 {DefaultIp}): ", DefaultIp);
            Console.WriteLine($"当前 PLC IP: {_plcIp}");
            Console.WriteLine();

            while (true) {
                Console.WriteLine("========= 主菜单 =========");
                Console.WriteLine($"  当前目标: {_plcIp}");
                Console.WriteLine("  1 - 轮询读取连续字节块");
                Console.WriteLine("  2 - 写入数据");
                Console.WriteLine("  3 - 读取单个字节");
                Console.WriteLine("  4 - 修改 PLC IP");
                Console.WriteLine("  0 - 退出");
                Console.Write("请选择操作 (0-4): ");

                string choice = Console.ReadLine()?.Trim();
                Console.WriteLine();

                switch (choice) {
                    case "1":
                        RunReadBlock();
                        break;
                    case "2":
                        RunWrite();
                        break;
                    case "3":
                        RunReadSingleByte();
                        break;
                    case "4":
                        ChangeIp();
                        break;
                    case "0":
                        return;
                    default:
                        Console.WriteLine("无效输入，请重新选择。");
                        break;
                }
            }
        }

        // ============================================================
        // 修改 IP（可选功能，避免"关掉重开"才能换目标）
        // ============================================================
        static void ChangeIp() {
            _plcIp = PromptString($"新 PLC IP (当前 {_plcIp}): ", _plcIp);
            Console.WriteLine($"已切换 PLC IP: {_plcIp}");
        }

        // ============================================================
        // 模式 1：轮询读取连续字节块
        // ============================================================
        static void RunReadBlock() {
            int startByte = PromptInt("起始字节偏移 (默认 1200): ", DefaultStartByte, 0, 65535);
            int length = PromptInt("读取长度(字节，默认 40): ", DefaultReadLength, 1, 2000);
            int interval = PromptInt("轮询间隔(ms，默认 1000): ", DefaultPollIntervalMs, 50, 60000);

            Console.WriteLine();
            Console.WriteLine($"开始轮询：{_plcIp}  DB1.DBB{startByte}  长度 {length}  间隔 {interval}ms");
            Console.WriteLine("按 Ctrl+C 停止轮询并返回菜单。");
            Console.WriteLine();

            bool stopFlag = false;
            ConsoleCancelEventHandler handler = (s, e) => {
                e.Cancel = true;   // 阻止进程退出
                stopFlag = true;
            };
            Console.CancelKeyPress += handler;

            Plc plc = null;
            try {
                while (!stopFlag) {
                    try {
                        // ⭐ 复用全局 _plcIp
                        if (plc == null || !plc.IsConnected)
                            plc = Connect(_plcIp);

                        byte[] data = plc.ReadBytes(S7DataType.DataBlock, DbNumber, startByte, length);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 读取成功 {data.Length} 字节");
                        ParseAndPrint(data, startByte);
                    } catch (PlcException ex) {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] PLC通信异常: {ex.Message}，3秒后重连...");
                        SafeClose(plc);
                        plc = null;
                        Thread.Sleep(3000);
                        continue;
                    } catch (Exception ex) {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 未知异常: {ex.Message}，3秒后重连...");
                        SafeClose(plc);
                        plc = null;
                        Thread.Sleep(3000);
                        continue;
                    }

                    // 分段 sleep，保证 Ctrl+C 后能快速退出
                    int waited = 0;
                    while (waited < interval && !stopFlag) {
                        int step = Math.Min(100, interval - waited);
                        Thread.Sleep(step);
                        waited += step;
                    }
                }
            } finally {
                Console.CancelKeyPress -= handler;
                SafeClose(plc);
                Console.WriteLine();
                Console.WriteLine("已停止轮询。");
            }
        }

        // ============================================================
        // 模式 2：写入数据
        // ============================================================
        static void RunWrite() {
            int startByte = PromptInt("起始字节偏移 (默认 0): ", 0, 0, 65535);
            int length = PromptInt("数据长度(字节): ", 1, 1, 2000);

            byte[] payload = null;
            while (payload == null) {
                Console.WriteLine();
                Console.WriteLine("请输入要写入的内容，支持两种格式：");
                Console.WriteLine("  [十六进制]  例如: 01 0A FF  （每字节两位 hex，空格分隔）");
                Console.WriteLine("  [文本]      例如: \"Hello\"   （用双引号包裹，ASCII 编码）");
                Console.Write("内容: ");
                string content = Console.ReadLine() ?? "";

                payload = ParsePayload(content, length, out string err);
                if (payload == null)
                    Console.WriteLine("解析失败：" + err);
            }

            Console.WriteLine();
            Console.WriteLine($"将写入 {payload.Length} 字节到 {_plcIp} 的 DB1.DBB{startByte}:");
            Console.WriteLine("  " + BitConverter.ToString(payload).Replace("-", " "));
            Console.Write("确认写入? (Y/N): ");
            string confirm = Console.ReadLine()?.Trim().ToUpper();
            if (confirm != "Y" && confirm != "YES") {
                Console.WriteLine("已取消写入。");
                return;
            }

            Plc plc = null;
            try {
                // ⭐ 复用全局 _plcIp
                plc = Connect(_plcIp);
                plc.WriteBytes(S7DataType.DataBlock, DbNumber, startByte, payload);
                Console.WriteLine($"写入成功：DB1.DBB{startByte} 起 {payload.Length} 字节");
            } catch (Exception ex) {
                Console.WriteLine($"写入失败: {ex.Message}");
            } finally {
                SafeClose(plc);
            }
        }

        // ============================================================
        // 模式 3：读取单个字节
        // ============================================================
        static void RunReadSingleByte() {
            Console.Write("字节号 (VBxx，例如 1205 或 VB1205): ");
            string raw = Console.ReadLine()?.Trim() ?? "";
            if (raw.StartsWith("VB", StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(2);

            if (!int.TryParse(raw, out int byteAddr) || byteAddr < 0 || byteAddr > 65535) {
                Console.WriteLine("无效的字节号。");
                return;
            }

            Plc plc = null;
            try {
                // ⭐ 复用全局 _plcIp
                plc = Connect(_plcIp);
                byte[] data = plc.ReadBytes(S7DataType.DataBlock, DbNumber, byteAddr, 1);
                if (data == null || data.Length < 1) {
                    Console.WriteLine("读取失败，未返回数据。");
                    return;
                }

                byte b = data[0];
                string bits = Convert.ToString(b, 2).PadLeft(8, '0');

                Console.WriteLine();
                Console.WriteLine($"VB{byteAddr} = {b}  (0x{b:X2}, 0b{bits})");
                Console.WriteLine("各位状态：");
                for (int i = 0; i < 8; i++) {
                    bool bit = (b & (1 << i)) != 0;
                    Console.WriteLine($"  V{byteAddr}.{i} = {bit}");
                }
            } catch (Exception ex) {
                Console.WriteLine($"读取失败: {ex.Message}");
            } finally {
                SafeClose(plc);
            }
        }

        // ============================================================
        // 辅助方法
        // ============================================================

        /// <summary>
        /// 建立 PLC 连接。
        /// S7-200 SMART 使用 CpuType.S71200 兼容性最稳，Rack=0, Slot=1。
        /// </summary>
        static Plc Connect(string ip) {
            var plc = new Plc(CpuType.S71200, ip, 0, 1);
            plc.Open();
            if (!plc.IsConnected)
                throw new Exception("连接未建立");
            Console.WriteLine($"已连接 {ip}（CpuType.S71200, Rack=0, Slot=1）");
            return plc;
        }

        static void SafeClose(Plc plc) {
            try { plc?.Close(); } catch { /* 忽略 */ }
        }

        /// <summary>通用解析 & 打印（起始地址可变）</summary>
        static void ParseAndPrint(byte[] data, int startByte) {
            if (data == null || data.Length == 0)
                return;

            Console.WriteLine($"  Raw HEX: {BitConverter.ToString(data).Replace("-", " ")}");

            if (data.Length >= 4) {
                float realVal = S7.Net.Types.Real.FromByteArray(Slice(data, 0, 4));
                Console.WriteLine($"  VB{startByte} (Real)   = {realVal}");
            }
            if (data.Length >= 2) {
                short intVal = S7.Net.Types.Int.FromByteArray(Slice(data, 0, 2));
                Console.WriteLine($"  VB{startByte} (Int)    = {intVal}");
            }
            if (data.Length >= 4) {
                int dintVal = S7.Net.Types.DInt.FromByteArray(Slice(data, 0, 4));
                Console.WriteLine($"  VB{startByte} (DInt)   = {dintVal}");
            }
            if (data.Length >= 2) {
                ushort wordVal = S7.Net.Types.Word.FromByteArray(Slice(data, 0, 2));
                Console.WriteLine($"  VB{startByte} (Word)   = {wordVal}");
            }

            // 尝试按 S7 String 解析（前2字节：最大长度、当前长度）
            if (data.Length >= 2) {
                int curLen = data[1];
                if (curLen > 0 && curLen <= 254 && 2 + curLen <= data.Length) {
                    try {
                        string strVal = Encoding.ASCII.GetString(data, 2, curLen);
                        Console.WriteLine($"  VB{startByte} (String) = \"{strVal}\"");
                    } catch { /* 忽略 */ }
                }
            }
            Console.WriteLine();
        }

        /// <summary>
        /// 解析用户输入内容为字节数组。
        /// - 双引号包裹 → 强制按 ASCII 文本处理；
        /// - 全部为两位十六进制 token → 按十六进制处理，字节数必须等于 expectedLength；
        /// - 其它 → 按 ASCII 文本处理，按 expectedLength 截断或补 0。
        /// </summary>
        static byte[] ParsePayload(string content, int expectedLength, out string error) {
            error = null;
            string text = (content ?? "").Trim();

            // 双引号强制文本
            bool forceText = text.Length >= 2 && text.StartsWith("\"") && text.EndsWith("\"");
            if (forceText)
                text = text.Substring(1, text.Length - 2);

            // 尝试识别十六进制
            bool looksHex = !forceText;
            var hexBytes = new List<byte>();
            if (looksHex) {
                var tokens = text.Split(new[] { ' ', ',', '\t', ';' },
                                        StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                    looksHex = false;

                foreach (var rawToken in tokens) {
                    string token = rawToken;
                    if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        token = token.Substring(2);

                    if (token.Length != 2 || !IsHex(token)) {
                        looksHex = false;
                        break;
                    }
                    hexBytes.Add(Convert.ToByte(token, 16));
                }
            }

            // 十六进制模式：长度必须严格一致
            if (looksHex) {
                if (hexBytes.Count != expectedLength) {
                    error = $"十六进制解析出 {hexBytes.Count} 字节，与指定长度 {expectedLength} 不一致。";
                    return null;
                }
                return hexBytes.ToArray();
            }

            // 文本模式：截断 / 补 0
            byte[] ascii = Encoding.ASCII.GetBytes(text);
            var result = new byte[expectedLength];
            int copy = Math.Min(ascii.Length, expectedLength);
            Array.Copy(ascii, result, copy);

            if (ascii.Length > expectedLength)
                Console.WriteLine($"提示：文本长度 {ascii.Length} 超过指定长度 {expectedLength}，已截断。");
            else if (ascii.Length < expectedLength)
                Console.WriteLine($"提示：文本长度 {ascii.Length} 小于指定长度 {expectedLength}，末尾补 0。");

            return result;
        }

        static bool IsHex(string s) {
            foreach (char c in s) {
                if (!((c >= '0' && c <= '9') ||
                      (c >= 'a' && c <= 'f') ||
                      (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }

        /// <summary>取数组指定段</summary>
        static byte[] Slice(byte[] src, int offset, int length) {
            var dst = new byte[length];
            Array.Copy(src, offset, dst, 0, length);
            return dst;
        }

        static string PromptString(string prompt, string defaultValue) {
            Console.Write(prompt);
            string s = Console.ReadLine();
            return string.IsNullOrWhiteSpace(s) ? defaultValue : s.Trim();
        }

        static int PromptInt(string prompt, int defaultValue, int min, int max) {
            while (true) {
                Console.Write(prompt);
                string s = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(s))
                    return defaultValue;

                if (int.TryParse(s.Trim(), out int v) && v >= min && v <= max)
                    return v;

                Console.WriteLine($"输入无效，请输入 {min} ~ {max} 之间的整数。");
            }
        }
    }
}