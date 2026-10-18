"""Build a local audio/text review sheet without promoting draft references to truth."""

import argparse
import html
import json
import pathlib

from common import non_system_path, sha256


def main():
    """Keep all model outputs visible, with provenance and a concise human checklist."""
    parser = argparse.ArgumentParser()
    for name in ("corpus", "results", "translations", "output"):
        parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    corpus = json.loads(pathlib.Path(args.corpus).read_text(encoding="utf-8"))
    results = json.loads(pathlib.Path(args.results).read_text(encoding="utf-8"))
    translations = json.loads(pathlib.Path(args.translations).read_text(encoding="utf-8"))
    esc = html.escape
    parts = ['<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>LiveLingo M2 质量核对</title>',
             '<style>body{font:17px/1.7 system-ui;max-width:1100px;margin:32px auto;padding:0 20px;background:#f7f8fb;color:#182537}section{background:white;border:1px solid #cbd2dc;border-radius:10px;padding:22px;margin:24px 0}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ccd;padding:10px;text-align:left;vertical-align:top}audio{width:100%}.note{color:#655019}code{overflow-wrap:anywhere}</style>',
             '<h1>LiveLingo M2：集中质量核对</h1><p>官方 JLPT 2018 N2 脚本作为参考来源；片段边界和中文草稿尚未人工确认。所有原始输出保留，未计质量通过。</p>',
             '<p>依次播放每段：①是否缺开头或句尾；②日语参考是否与听到的一致；③重点检查否定、重复称呼、人名；④比较两种模型译文，指出漏译、误译或擅加语气。回复条目 ID 和需修正内容即可。</p>',
             '<p>本页不上传数据、不启动服务。音频来自 D 盘专用素材目录；删除素材后音频链接失效，报告和文字仍保留。</p>']
    for item in corpus["items"]:
        audio = pathlib.Path(item["audioPath"])
        if sha256(audio) != item["inputSha256"]:
            raise ValueError("Review audio fingerprint mismatch: " + item["id"])
        parts.append(f'<section><h2>{esc(item["id"])}</h2><audio controls preload="none" src="{esc(audio.as_uri(), quote=True)}"></audio>')
        parts.append(f'<p class="note">{esc(item.get("referenceStatus", "unverified"))}；官方脚本第 {item["page"]} 页。</p>')
        parts.append(f'<p><b>日语参考：</b>{esc(item["referenceText"])}</p><p><b>中文核对草稿：</b>{esc(item["referenceChinese"])}</p>')
        for kind in ("qwen", "whisper"):
            row = results.get(kind, {}).get(item["id"], {})
            parts.append(f'<p><b>{kind} ASR：</b>{esc(row.get("text", "未执行"))}</p>')
        parts.append('<table><tr><th>输入来源</th><th>TranslateGemma 4B</th><th>TranslateGemma 12B</th></tr>')
        for origin, label in (("reference", "官方参考"), ("qwen", "Qwen 识别"), ("whisper", "Whisper 识别")):
            cells = []
            for model in ("translategemma:4b", "translategemma:12b"):
                rows = [r for m in translations["models"] if m["model"] == model for r in m["runs"]
                        if r["id"] == item["id"] + "-" + origin]
                text = rows[0]["response"].get("message", {}).get("content", "") if rows else "未执行"
                cells.append('<td>' + esc(text) + '</td>')
            parts.append('<tr><th>' + label + '</th>' + ''.join(cells) + '</tr>')
        parts.append(f'</table><p><small>音频 SHA-256：<code>{esc(item["inputSha256"])}</code></small></p></section>')
    parts.append('</html>')
    output = non_system_path(args.output)
    with output.open("x", encoding="utf-8", newline="\n") as target:
        target.write('\n'.join(parts) + '\n')
    print(output)


if __name__ == "__main__":
    main()
