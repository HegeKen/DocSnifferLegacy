//! 内容提取：纯文本（自动编码检测）、Office(DOCX/XLSX/PPTX/WPS)、PDF。
//! 遵循 Legacy 版定位 —— 仅提取纯文本摘要，舍弃样式、图片、表格结构解析。
//! WPS 专有格式支持：.wps（文字）、.dps（演示）、.et（表格）兼存 OOXML(zip) 与
//! 老版二进制 OLE2/CFB 两种形态，此处按 CFB 魔数分流到对应二进制解析器。

use chardetng::EncodingDetector;
use std::collections::{HashMap, HashSet};
use std::path::Path;

/// 返回文档的纯文本内容；无法提取时返回空串（不视为错误）。
pub fn extract_text(path: &Path) -> String {
    let ext = path
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
        .to_ascii_lowercase();

    match ext.as_str() {
        // Office (OOXML)：docx / xlsx / pptx / docm / xlsm / pptm
        "docx" | "xlsx" | "pptx" | "docm" | "xlsm" | "pptm" => extract_office_xml(path),
        "pdf" => extract_pdf(path),
        // WPS 专有：老版二进制(OLE2/CFB) 与 OOXML(zip) 并存，按魔数分流
        "wps" | "dps" | "et" => extract_wps_legacy(path),
        // 其余一律按纯文本处理（含代码文件、未知类型）
        _ => extract_plain(path),
    }
}

/// WPS 专有扩展名：先按 CFB 魔数判断是否为老版二进制，否则回退 OOXML/纯文本。
fn extract_wps_legacy(path: &Path) -> String {
    let Ok(bytes) = std::fs::read(path) else {
        return String::new();
    };
    if is_cfb(&bytes) {
        return extract_cfb(&bytes);
    }
    // 非二进制：现代 WPS 为 OOXML zip；解析不到则退化为纯文本扫描
    let xml = extract_office_xml(path);
    if xml.is_empty() {
        extract_plain(path)
    } else {
        xml
    }
}

/// 按内部流类型选择对应的 OLE2 二进制解析器。
fn extract_cfb(bytes: &[u8]) -> String {
    let streams = parse_cfb_streams(bytes);
    if streams.contains_key("WordDocument") {
        extract_wps_binary(&streams)
    } else if streams.contains_key("PowerPoint Document") {
        extract_dps_binary(&streams)
    } else if streams.contains_key("Workbook") || streams.contains_key("Book") {
        extract_et_binary(&streams)
    } else {
        String::new()
    }
}

/// 是否为“值得提取内容”的文本类扩展名。
pub fn is_textual_ext(ext: &str) -> bool {
    let e = ext.trim_start_matches('.').to_ascii_lowercase();
    matches!(
        e.as_str(),
        "txt" | "md" | "log" | "ini" | "cfg" | "conf" | "xml" | "json" | "yaml" | "yml"
            | "csv" | "tsv" | "sql" | "html" | "htm" | "css" | "js" | "ts" | "py" | "rs"
            | "cpp" | "c" | "h" | "hpp" | "java" | "go" | "php" | "bash" | "sh" | "bat" | "ps1"
            | "properties" | "readme" | "xls" | "xlsx" | "doc" | "docx" | "ppt" | "pptx"
            | "wps" | "dps" | "et" | "rtf" | "pdf"
    )
}

/// 纯文本：字节 → 编码检测 → 解码。
fn extract_plain(path: &Path) -> String {
    let Ok(bytes) = std::fs::read(path) else {
        return String::new();
    };
    if bytes.is_empty() {
        return String::new();
    }
    decode_bytes(&bytes)
}

/// 使用 chardetng 检测编码，encoding_rs 解码；默认回退 GB18030（内网中文历史文档高频）。
fn decode_bytes(bytes: &[u8]) -> String {
    let mut detector = EncodingDetector::new();
    detector.feed(bytes, true);
    let encoding = detector.guess(None, true);
    let (text, _, _) = encoding.decode(bytes);
    text.into_owned()
}

/// 提取文档内容（Office 为 zip 中多个 XML 拼接）。
fn extract_office_xml(path: &Path) -> String {
    let Ok(file) = std::fs::File::open(path) else {
        return String::new();
    };
    let Ok(mut archive) = zip::ZipArchive::new(file) else {
        // 非 zip（如老版二进制 WPS）由调用方先按魔数分流，此处仅兜底纯文本扫描
        return extract_plain(path);
    };

    let names: Vec<String> = {
        let mut v = Vec::new();
        for i in 0..archive.len() {
            if let Ok(entry) = archive.by_index(i) {
                // 记录 XML/共享字符串类条目名
                let name = entry.name().to_lowercase();
                if name.ends_with(".xml") || name.ends_with(".rels") {
                    v.push(entry.name().to_string());
                }
            }
        }
        v
    };

    let mut out = String::new();
    for name in names {
        let mut entry = match archive.by_name(&name) {
            Ok(e) => e,
            Err(_) => continue,
        };
        let mut buf = Vec::new();
        if std::io::Read::read_to_end(&mut entry, &mut buf).is_err() {
            continue;
        }
        let xml = decode_bytes(&buf);
        let fragments = extract_xml_text(&xml);
        if !fragments.is_empty() {
            out.push_str(fragments.as_str());
            out.push('\n');
        }
    }
    out
}

/// 从 XML 中抽取关注标签的文本内容。
/// 关注：word <w:t>、ppt <a:t>、xlsx <t> 与 <v>（单元格值）。
fn extract_xml_text(xml: &str) -> String {
    let mut out = String::new();
    for tag in ["w:t", "a:t", "t", "v"] {
        collect_tag(&xml, tag, &mut out);
    }
    // 去除重复抽取造成的冗余（按标签出现顺序已可控，这里仅折叠空白）。
    out.split_whitespace()
        .collect::<Vec<_>>()
        .join(" ")
}

/// 提取 `<(?:tag) ...>inner</(?:tag)>` 的内容。
fn collect_tag(xml: &str, tag: &str, out: &mut String) {
    let mut rest = xml;
    loop {
        let Some(open) = rest.find(&format!("<{tag}")) else {
            break;
        };
        // 找到 `>`，再从该位置找闭合标签
        let after_open = &rest[open..];
        let after_gt = match after_open.find('>') {
            Some(pos) => open + pos + 1,
            None => break,
        };
        let close_tag = format!("</{tag}>");
        let body = &rest[after_gt..];
        let Some(close) = body.find(&close_tag) else {
            break;
        };
        if close > 0 {
            out.push_str(&body[..close]);
            out.push(' ');
        }
        rest = &rest[after_gt + close + close_tag.len()..];
    }
}

/// 轻量 PDF 纯文本提取：解析 `(str) Tj` / `[...] TJ` 文本操作符。
/// 自动解压 FlateDecode 流；压缩不可解时回退解压前扫描。
fn extract_pdf(path: &Path) -> String {
    let Ok(bytes) = std::fs::read(path) else {
        return String::new();
    };
    let mut out = String::new();
    let mut pos = 0usize;

    // 逐段切出 stream ... endstream
    while let Some(rel_stream) = find_subslice(&bytes[pos..], b"stream") {
        let stream_start = pos + rel_stream;
        let mut data_start = stream_start + b"stream".len();
        // 跳过 stream 后的换行符（\r\n 或 \n）
        if data_start < bytes.len() && bytes[data_start] == b'\r' {
            data_start += 1;
        }
        if data_start < bytes.len() && bytes[data_start] == b'\n' {
            data_start += 1;
        }
        let Some(rel_end) = find_subslice(&bytes[data_start..], b"endstream") else {
            break;
        };
        let data_end = data_start + rel_end;
        let raw = &bytes[data_start..data_end];

        // 判断当前流是否 FlateDecode（向前扫描最近的 dict 片段）
        let ctx = &bytes[..stream_start];
        let prev = &ctx[ctx.len().saturating_sub(512)..];
        let flate = String::from_utf8_lossy(prev)
            .to_uppercase()
            .contains("FLATEDECODE");

        let content: Option<Vec<u8>> = if flate {
            decompress_flate(raw).ok()
        } else {
            Some(raw.to_vec())
        };
        if let Some(content) = content {
            out.push_str(&extract_pdf_operators(&content));
            out.push('\n');
        }
        pos = data_end + b"endstream".len();
    }
    out
}

fn decompress_flate(raw: &[u8]) -> std::io::Result<Vec<u8>> {
    let mut decoder = flate2::read::ZlibDecoder::new(raw);
    let mut out = Vec::new();
    std::io::Read::read_to_end(&mut decoder, &mut out)?;
    Ok(out)
}

fn find_subslice(haystack: &[u8], needle: &[u8]) -> Option<usize> {
    if needle.is_empty() || haystack.len() < needle.len() {
        return None;
    }
    haystack
        .windows(needle.len())
        .position(|w| w == needle)
}

/// 从 PDF 内容字节中抽取文本操作符。
fn extract_pdf_operators(content: &[u8]) -> String {
    let s = String::from_utf8_lossy(content);
    let mut out = String::new();

    // 处理 `( ... ) Tj` 与 `[ (..) (..) ] TJ`
    extract_pdf_strings(&s, &mut out);
    out
}

/// 扫描 PDF 字符串字面量 `( ... )`，并略过紧跟其后的操作符判断。
fn extract_pdf_strings(s: &str, out: &mut String) {
    let bytes = s.as_bytes();
    let mut i = 0usize;
    let n = bytes.len();
    while i < n {
        match bytes[i] {
            b'(' => {
                // 找到匹配的右括号（支持转义与嵌套括号）
                let mut depth = 1i32;
                let mut j = i + 1;
                while j < n && depth > 0 {
                    match bytes[j] {
                        b'\\' => j += 2, // 跳过转义字符
                        b'(' => {
                            depth += 1;
                            j += 1;
                        }
                        b')' => {
                            depth -= 1;
                            j += 1;
                        }
                        _ => j += 1,
                    }
                }
                if depth == 0 {
                    let literal = &s[i..j];
                    out.push_str(&unescape_pdf_string(literal));
                    out.push(' ');
                    i = j;
                } else {
                    i += 1;
                }
            }
            b'<' => {
                // 十六进制字符串 <...>
                if let Some(end) = s[i..].find('>') {
                    let hex = &s[i + 1..i + end];
                    if let Some(decoded) = decode_hex(hex) {
                        out.push_str(&String::from_utf8_lossy(&decoded));
                        out.push(' ');
                    }
                    i = i + end + 1;
                } else {
                    i += 1;
                }
            }
            _ => i += 1,
        }
    }
}

fn decode_hex(hex: &str) -> Option<Vec<u8>> {
    let cleaned: String = hex.chars().filter(|c| !c.is_whitespace()).collect();
    if cleaned.len() % 2 != 0 {
        return None;
    }
    let mut bytes = Vec::with_capacity(cleaned.len() / 2);
    let b = cleaned.as_bytes();
    for pair in b.chunks(2) {
        let hi = hex_val(pair[0])?;
        let lo = hex_val(pair[1])?;
        bytes.push(hi << 4 | lo);
    }
    Some(bytes)
}

fn hex_val(c: u8) -> Option<u8> {
    match c {
        b'0'..=b'9' => Some(c - b'0'),
        b'a'..=b'f' => Some(c - b'a' + 10),
        b'A'..=b'F' => Some(c - b'A' + 10),
        _ => None,
    }
}

/// 解析 PDF 字符串字面量转义。
fn unescape_pdf_string(literal: &str) -> String {
    let inner = &literal[1..literal.len().saturating_sub(1)];
    let mut out = String::new();
    let mut chars = inner.chars();
    while let Some(c) = chars.next() {
        if c == '\\' {
            match chars.next() {
                Some('n') => out.push('\n'),
                Some('r') => out.push('\r'),
                Some('t') => out.push('\t'),
                Some('b') => out.push('\u{8}'),
                Some('f') => out.push('\u{c}'),
                Some('(') => out.push('('),
                Some(')') => out.push(')'),
                Some('\\') => out.push('\\'),
                Some(c @ '0'..='7') => {
                    // 八进制转义（最多 3 位）
                    let mut val = c.to_digit(8).unwrap_or(0);
                    for _ in 0..2 {
                        if let Some(n @ '0'..='7') = chars.clone().next() {
                            chars.next();
                            val = val * 8 + n.to_digit(8).unwrap_or(0);
                        } else {
                            break;
                        }
                    }
                    if let Some(ch) = char::from_u32(val) {
                        out.push(ch);
                    }
                }
                _ => {}
            }
        } else {
            out.push(c);
        }
    }
    out
}

// ===========================================================================
// 老版 WPS 专有二进制（OLE2 / CFB）解析：.wps（Word 类）/ .dps（PPT 类）/ .et（XLS 类）
// ===========================================================================

/// OLE2 / Compound File Binary (CFB) 魔数：d0 cf 11 e0 a1 b1 1a e1
const CFB_MAGIC: [u8; 8] = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

fn is_cfb(bytes: &[u8]) -> bool {
    bytes.len() >= 8 && bytes[..8] == CFB_MAGIC
}

/// 读小端 u16，越界返回 0。
fn peek_u16(b: &[u8], off: usize) -> usize {
    if off + 2 <= b.len() {
        u16::from_le_bytes([b[off], b[off + 1]]) as usize
    } else {
        0
    }
}

/// 读小端 u32，越界返回 0xFFFF_FFFF（CFB 的“无扇区”哨兵值）。
fn peek_u32(b: &[u8], off: usize) -> usize {
    if off + 4 <= b.len() {
        u32::from_le_bytes([b[off], b[off + 1], b[off + 2], b[off + 3]]) as usize
    } else {
        0xFFFF_FFFF
    }
}

/// 读小端 u64 并截断为 usize，越界返回 0。
fn peek_u64(b: &[u8], off: usize) -> usize {
    if off + 8 <= b.len() {
        u64::from_le_bytes([
            b[off],
            b[off + 1],
            b[off + 2],
            b[off + 3],
            b[off + 4],
            b[off + 5],
            b[off + 6],
            b[off + 7],
        ]) as usize
    } else {
        0
    }
}

/// UTF-16LE 解码。
fn decode_utf16le(b: &[u8]) -> String {
    let (s, _, _) = encoding_rs::UTF_16LE.decode(b);
    s.into_owned()
}

/// GB18030 解码（覆盖 ANSI/ASCII + 全中文）。
fn decode_gb18030(b: &[u8]) -> String {
    let (s, _, _) = encoding_rs::GB18030.decode(b);
    s.into_owned()
}

/// 取出指定扇区字节；越界返回空切片。扇区按 (编号+1)*48 字节对齐（略过头部）。
fn sector_at<'a>(data: &'a [u8], sec: usize, sector_size: usize) -> &'a [u8] {
    let s = (sec + 1) * sector_size;
    if s + sector_size <= data.len() {
        &data[s..s + sector_size]
    } else {
        &[]
    }
}

/// 解析 CFB 复合文档，返回各内部流（名称 -> 内容）。
fn parse_cfb_streams(data: &[u8]) -> HashMap<String, Vec<u8>> {
    let mut streams = HashMap::new();
    if data.len() < 0x4C {
        return streams;
    }
    let sector_size = 1usize << peek_u16(data, 0x1E).min(20);
    let mini_sector_size = 1usize << peek_u16(data, 0x20).min(20);
    let first_dir_sector = peek_u32(data, 0x30);
    let mini_stream_cutoff = peek_u32(data, 0x38);
    let first_minifat_sector = peek_u32(data, 0x3C);
    let num_difat_sectors = peek_u32(data, 0x44);
    let first_difat_sector = peek_u32(data, 0x48);
    if sector_size < 2 || mini_sector_size < 2 {
        return streams;
    }

    // 头部的 DIFAT（109 项）
    let mut difat = Vec::new();
    for i in 0..109 {
        let v = peek_u32(data, 0x4C + i * 4);
        if v < 0xFFFF_FFFE {
            difat.push(v);
        }
    }
    // DIFAT 链（每扇区末 4 字节为下一 DIFAT 扇区）
    if num_difat_sectors > 0 && first_difat_sector < 0xFFFF_FFFE {
        let mut seen = HashSet::new();
        let mut cur = first_difat_sector;
        while !seen.contains(&cur) && cur < 0xFFFF_FFFE {
            seen.insert(cur);
            let s = sector_at(data, cur, sector_size);
            if s.is_empty() {
                break;
            }
            let n = sector_size / 4 - 1;
            for i in 0..n {
                let v = peek_u32(s, i * 4);
                if v < 0xFFFF_FFFE {
                    difat.push(v);
                }
            }
            cur = peek_u32(s, n * 4);
        }
    }

    // 构建 FAT
    let mut fat = Vec::new();
    for &fs in &difat {
        let s = sector_at(data, fs, sector_size);
        if s.is_empty() {
            continue;
        }
        for i in 0..sector_size / 4 {
            fat.push(peek_u32(s, i * 4));
        }
    }

    // 读取目录流
    let mut dir_bytes = Vec::new();
    {
        let mut seen = HashSet::new();
        let mut sec = first_dir_sector;
        while !seen.contains(&sec) && sec < 0xFFFF_FFFE {
            seen.insert(sec);
            let s = sector_at(data, sec, sector_size);
            if s.is_empty() {
                break;
            }
            dir_bytes.extend_from_slice(s);
            let nxt = fat.get(sec).copied().unwrap_or(0xFFFF_FFFE);
            if nxt >= 0xFFFF_FFFE {
                break;
            }
            sec = nxt;
        }
    }

    // 目录条目
    struct DirEntry {
        name: String,
        obj_type: u8,
        start: usize,
        size: usize,
    }
    let mut entries = Vec::new();
    let mut i = 0;
    while i + 128 <= dir_bytes.len() {
        let e = &dir_bytes[i..i + 128];
        let name_len = peek_u16(e, 0x40);
        let obj_type = e[0x42];
        if name_len >= 2 {
            let raw_name = &e[..(name_len - 2).min(64)];
            let name = decode_utf16le(raw_name);
            let start = peek_u32(e, 0x74);
            let size = peek_u64(e, 0x78);
            entries.push(DirEntry {
                name,
                obj_type,
                start,
                size,
            });
        }
        i += 128;
    }

    // 根目录（obj_type == 5）的扇区链即迷你流
    let mut mini_stream = Vec::new();
    if let Some(root) = entries.iter().find(|e| e.obj_type == 5) {
        let mut seen = HashSet::new();
        let mut sec = root.start;
        while !seen.contains(&sec) && sec < 0xFFFF_FFFE {
            seen.insert(sec);
            let s = sector_at(data, sec, sector_size);
            if s.is_empty() {
                break;
            }
            mini_stream.extend_from_slice(s);
            let nxt = fat.get(sec).copied().unwrap_or(0xFFFF_FFFE);
            if nxt >= 0xFFFF_FFFE {
                break;
            }
            sec = nxt;
        }
    }

    // 迷你 FAT
    let mut minifat = Vec::new();
    if first_minifat_sector != 0xFFFF_FFFE && first_minifat_sector != 0xFFFF_FFFF {
        let mut seen = HashSet::new();
        let mut sec = first_minifat_sector;
        while !seen.contains(&sec) && sec < 0xFFFF_FFFE {
            seen.insert(sec);
            let s = sector_at(data, sec, sector_size);
            if s.is_empty() {
                break;
            }
            for i in 0..sector_size / 4 {
                minifat.push(peek_u32(s, i * 4));
            }
            let nxt = fat.get(sec).copied().unwrap_or(0xFFFF_FFFE);
            if nxt >= 0xFFFF_FFFE {
                break;
            }
            sec = nxt;
        }
    }

    // 读取各流
    for e in &entries {
        if e.obj_type == 2 {
            let in_mini = e.size < mini_stream_cutoff;
            let content = read_cfb_stream(
                data,
                &fat,
                &minifat,
                &mini_stream,
                e.size,
                e.start,
                in_mini,
                sector_size,
                mini_sector_size,
            );
            streams.insert(e.name.clone(), content);
        }
    }
    streams
}

/// 按扇区链读取一个流；迷你流走 mini FAT。
fn read_cfb_stream(
    data: &[u8],
    fat: &[usize],
    minifat: &[usize],
    mini_stream: &[u8],
    total: usize,
    start: usize,
    in_mini: bool,
    sector_size: usize,
    mini_sector_size: usize,
) -> Vec<u8> {
    let mut out = Vec::with_capacity(total);
    let mut seen = HashSet::new();
    let mut sec = start;
    if in_mini {
        while !seen.contains(&sec) && sec < 0xFFFF_FFFE && out.len() < total {
            seen.insert(sec);
            let off = sec * mini_sector_size;
            if off + mini_sector_size > mini_stream.len() {
                break;
            }
            out.extend_from_slice(&mini_stream[off..off + mini_sector_size]);
            let nxt = minifat.get(sec).copied().unwrap_or(0xFFFF_FFFE);
            if nxt >= 0xFFFF_FFFE {
                break;
            }
            sec = nxt;
        }
    } else {
        while !seen.contains(&sec) && sec < 0xFFFF_FFFE && out.len() < total {
            seen.insert(sec);
            let s = (sec + 1) * sector_size;
            if s < data.len() {
                let end = (s + sector_size).min(data.len());
                out.extend_from_slice(&data[s..end]);
            } else {
                break;
            }
            let nxt = fat.get(sec).copied().unwrap_or(0xFFFF_FFFE);
            if nxt >= 0xFFFF_FFFE {
                break;
            }
            sec = nxt;
        }
    }
    out.truncate(total);
    out
}

// ---- .wps（MS Word 类二进制）：WordDocument 流 + FIB 定位的 CLX 分段表 ----

fn extract_wps_binary(streams: &HashMap<String, Vec<u8>>) -> String {
    let Some(wd) = streams.get("WordDocument") else {
        return String::new();
    };
    let from_tbl0 = wps_extract_from_table(wd, streams.get("0Table").map(|v| v.as_slice()));
    if !from_tbl0.is_empty() {
        return from_tbl0;
    }
    wps_extract_from_table(wd, streams.get("1Table").map(|v| v.as_slice()))
}

/// 在表流（0Table/1Table）中定位类型 02 的 CLX（PlcPcd 分段表），解出各段文本。
fn wps_extract_from_table(wd: &[u8], tbl: Option<&[u8]>) -> String {
    let Some(tbl) = tbl else {
        return String::new();
    };
    let mut i = 0usize;
    while i + 5 <= tbl.len() {
        if tbl[i] == 0x02 {
            let lcb = peek_u32(tbl, i + 1);
            // PlcPcd 长度 = 4*(n+1) + 8*n = 12n+4
            if lcb >= 16 && i + 5 + lcb <= tbl.len() && (lcb - 4) % 12 == 0 {
                let n = (lcb - 4) / 12;
                if n >= 1 {
                    let plp = &tbl[i + 5..i + 5 + lcb];
                    let s = decode_plcpcd(wd, plp, n);
                    if !s.is_empty() {
                        return s;
                    }
                }
            }
        }
        i += 1;
    }
    String::new()
}

/// 解码 CLX 内的 PlcPcd：PCD 布局为 flags[0:2] + fc[2:6] + prm[6:8]。
fn decode_plcpcd(wd: &[u8], plp: &[u8], n: usize) -> String {
    if plp.len() < (n + 1) * 4 + n * 8 {
        return String::new();
    }
    let mut cps = Vec::with_capacity(n + 1);
    for k in 0..=n {
        cps.push(peek_u32(plp, k * 4));
    }
    let pcd_base = (n + 1) * 4;
    let mut out = String::new();
    for k in 0..n {
        let flags = peek_u16(plp, pcd_base + k * 8);
        let fc = peek_u32(plp, pcd_base + k * 8 + 2);
        let compressed = flags & 0x8000 != 0; // 位 15：压缩（单字节）标记
        let cplen = cps[k + 1].saturating_sub(cps[k]);
        if cplen == 0 {
            continue;
        }
        if compressed {
            if let Some(end) = fc.checked_add(cplen) {
                if end <= wd.len() {
                    out.push_str(&decode_gb18030(&wd[fc..end]));
                }
            }
        } else {
            let nb = cplen * 2;
            if let Some(end) = fc.checked_add(nb) {
                if end <= wd.len() {
                    out.push_str(&decode_utf16le(&wd[fc..end]));
                }
            }
        }
    }
    // 去掉文档末尾的段落标记 / 填充 NUL 等控制符
    out.trim_end_matches(|c: char| matches!(c, '\r' | '\n' | '\0'))
        .to_string()
}

// ---- .dps（MS PowerPoint 类二进制）：容器记录递归 + 文本原子 ----

fn extract_dps_binary(streams: &HashMap<String, Vec<u8>>) -> String {
    let Some(d) = streams.get("PowerPoint Document") else {
        return String::new();
    };
    let mut out = Vec::new();
    walk_ppt_records(d, 0, d.len(), &mut out);
    out.join("\n")
}

/// 递归遍历 PPT 记录：recVer==0xF 为容器，0x0FA0/0x03EE 为 Unicode 文本，0x0FA8 为单字节文本。
fn walk_ppt_records(d: &[u8], start: usize, end: usize, out: &mut Vec<String>) {
    let mut i = start;
    while i + 8 <= end {
        let rec = peek_u16(d, i);
        let ver = rec & 0xF;
        let typ = peek_u16(d, i + 2);
        let len = peek_u32(d, i + 4);
        let body = i + 8;
        let Some(b_end) = body.checked_add(len) else {
            break;
        };
        if b_end > end {
            break;
        }
        if ver == 0xF {
            walk_ppt_records(d, body, b_end, out);
        } else if ver == 0 && (typ == 0x0FA0 || typ == 0x03EE) {
            out.push(decode_utf16le(&d[body..b_end]));
        } else if ver == 0 && typ == 0x0FA8 {
            out.push(decode_gb18030(&d[body..b_end]));
        }
        i = b_end;
    }
}

// ---- .et（MS Excel 类二进制）：Workbook 流中的 SST（共享字符串表）记录 ----

fn extract_et_binary(streams: &HashMap<String, Vec<u8>>) -> String {
    let wb = match streams.get("Workbook").or_else(|| streams.get("Book")) {
        Some(wb) => wb,
        None => return String::new(),
    };
    let mut out = Vec::new();
    let mut i = 0usize;
    while i + 4 <= wb.len() {
        let rtype = peek_u16(wb, i);
        let rlen = peek_u16(wb, i + 2);
        let body = i + 4;
        let Some(b_end) = body.checked_add(rlen) else {
            break;
        };
        if b_end > wb.len() {
            break;
        }
        if rtype == 0x00FC {
            parse_sst_record(wb, body, b_end, &mut out);
        }
        i = b_end;
    }
    out.join("\n")
}

/// 解析 SST 记录（BIFF8）：每串前有 cch(u16) + grbit(u8)，高位字节标记确定宽/窄编码。
fn parse_sst_record(wb: &[u8], body: usize, b_end: usize, out: &mut Vec<String>) {
    if b_end < body + 8 {
        return;
    }
    let uniq = peek_u32(wb, body + 4);
    let mut p = body + 8;
    for _ in 0..uniq {
        if p + 4 > b_end {
            break;
        }
        let cch = peek_u16(wb, p);
        p += 2;
        let grbit = wb[p];
        p += 1;
        let f_high = grbit & 0x01 != 0;
        let f_rich = grbit & 0x04 != 0;
        let f_ext = grbit & 0x08 != 0;
        let nbytes = if f_high { cch * 2 } else { cch };
        if p + nbytes <= b_end {
            let seg = &wb[p..p + nbytes];
            let s = if f_high {
                decode_utf16le(seg)
            } else {
                decode_gb18030(seg)
            };
            out.push(s);
        }
        p += nbytes;
        if f_rich {
            if p + 2 > b_end {
                break;
            }
            let crun = peek_u16(wb, p);
            p += 2 + crun * 4;
            if p > b_end {
                break;
            }
        }
        if f_ext {
            if p + 4 > b_end {
                break;
            }
            let cbext = peek_u32(wb, p);
            p = cbext.saturating_add(p + 4);
            if p > b_end {
                break;
            }
        }
    }
}

#[cfg(test)]
mod wps_tests {
    use super::extract_text;
    use std::path::Path;

    #[test]
    fn extract_three_sample_files() {
        for f in ["测试一下DocSniffer.wps", "测试一下DocSniffer.dps", "测试一下DocSniffer.et"] {
            let p = Path::new(f);
            let out = extract_text(p);
            println!("== {} ==\n{}", f, out);
            assert!(
                out.contains("DocSniffer"),
                "{} 未能提取出 DocSniffer，输出: {:?}",
                f,
                out
            );
        }
    }
}
