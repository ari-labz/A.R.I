"""The benchmark tasks. Each has an id, a title, files to upload, messages to send, and check(ctx) -> [(what, ok)]."""
import io, os, random, re, subprocess, zipfile
from bench import office_text


def make_xlsx(rows):
    def cell(r, c, v):
        ref = f"{chr(65 + c)}{r}"
        return f'<c r="{ref}" t="inlineStr"><is><t>{v}</t></is></c>' if isinstance(v, str) else f'<c r="{ref}"><v>{v}</v></c>'
    sd = "".join(f'<row r="{i + 1}">' + "".join(cell(i + 1, c, v) for c, v in enumerate(row)) + "</row>" for i, row in enumerate(rows))
    b = io.BytesIO()
    with zipfile.ZipFile(b, "w") as z:
        z.writestr("[Content_Types].xml", '<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>')
        z.writestr("_rels/.rels", '<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>')
        z.writestr("xl/workbook.xml", '<?xml version="1.0"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>')
        z.writestr("xl/_rels/workbook.xml.rels", '<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>')
        z.writestr("xl/worksheets/sheet1.xml", f'<?xml version="1.0"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>{sd}</sheetData></worksheet>')
    return b.getvalue()


def sales():
    random.seed(3)
    rows, body = [["rep", "region", "month", "units", "revenue", "refunds"]], []
    for m in ["Jan", "Feb", "Mar", "Apr"]:
        for rep, reg in [("Dana", "North"), ("Omar", "South"), ("Priya", "North"), ("Leo", "West"), ("Mina", "South")]:
            u = random.randint(20, 90); body.append([rep, reg, m, u, u * random.randint(40, 60), random.randint(0, 6) * 40])
    random.shuffle(body)
    return rows + body, sum(r[4] for r in body), sum(r[5] for r in body)


SALES_ROWS, SALES_REV, SALES_REF = sales()
CV = "JORDAN PEARCE\njordan.pearce@example.com\n\nobjective\nlooking for a job where i can use my skills\n\nexperience\n2019-now BrightLeaf Garden Centre: customer service, till, ordering stock sometimes\n2017-2019 Dalton Cafe: coffees, cleaning\n\neducation\nGCSEs, BTEC business level 3\n"
FACTS = "Facts: at BrightLeaf, Jordan took over supplier ordering for indoor plants in 2022 and cut wastage from 18% to 9%. Trained 6 seasonal staff each spring. Wants a stock/purchasing assistant role. Full UK driving licence.\n"
NOTES_PDF_TEXT = "Quarterly stock note. The warehouse code for plants is WH-7731. Returns are handled on Fridays."


def pdf_bytes():
    objs = []
    stream = f"BT /F1 12 Tf 72 720 Td ({NOTES_PDF_TEXT}) Tj ET".encode()
    objs.append(b"<< /Type /Catalog /Pages 2 0 R >>"); objs.append(b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
    objs.append(b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>")
    objs.append(b"<< /Length " + str(len(stream)).encode() + b" >>\nstream\n" + stream + b"\nendstream"); objs.append(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    out = b"%PDF-1.4\n"; offs = []
    for i, o in enumerate(objs): offs.append(len(out)); out += f"{i + 1} 0 obj\n".encode() + o + b"\nendobj\n"
    x = len(out); out += f"xref\n0 {len(objs) + 1}\n0000000000 65535 f \n".encode() + b"".join(f"{o:010d} 00000 n \n".encode() for o in offs)
    return out + f"trailer\n<< /Size {len(objs) + 1} /Root 1 0 R >>\nstartxref\n{x}\n%%EOF".encode()


def alive(pattern): return bool(subprocess.run(f"ps aux | grep '[{pattern[0]}]{pattern[1:]}'", shell=True, capture_output=True, text=True).stdout.strip())


def pages_of(path): return len(re.findall(rb"/Type\s*/Page\b", open(path, "rb").read()))


TASKS = [
    {"id": "sheet-read", "title": "Read a spreadsheet and report the right totals", "files": {"sales.xlsx": lambda: make_xlsx(SALES_ROWS)},
     "messages": ["What are the total revenue and total refunds in sales.xlsx? Give me the two numbers."],
     "check": lambda c: [("total revenue is correct", f"{SALES_REV:,}" in c["replies"][0] or str(SALES_REV) in c["replies"][0]),
                         ("total refunds is correct", f"{SALES_REF:,}" in c["replies"][0] or str(SALES_REF) in c["replies"][0]),
                         ("original untouched", c["run"].same("sales.xlsx"))]},
    {"id": "sheet-rearrange", "title": "Rearrange a spreadsheet into a better-reading new file", "files": {"sales.xlsx": lambda: make_xlsx(SALES_ROWS)},
     "messages": ["Rearrange the data in sales.xlsx so it reads better (sorted sensibly, with totals) and give me the new file. Don't ask questions, just do it."],
     "check": lambda c: [("original untouched", c["run"].same("sales.xlsx")),
                         ("a new xlsx exists", any(f.endswith(".xlsx") and f != "sales.xlsx" for f in os.listdir(c["run"].folder))),
                         ("new file contains the totals", any(str(SALES_REV) in office_text(c["run"].file(f)) for f in os.listdir(c["run"].folder) if f.endswith(".xlsx") and f != "sales.xlsx") or "formula" in "")]},
    {"id": "cv-word", "title": "Turn a bad CV into a Word document, keeping the original", "files": {"cv.txt": CV, "facts.txt": FACTS},
     "messages": ["cv.txt is a friend's CV and facts.txt has extra info. Make a much better CV as a Word document for a purchasing assistant role. Don't ask questions; use only what's in the files and tell me anything you assumed."],
     "check": lambda c: [("original untouched", c["run"].same("cv.txt")),
                         ("a .docx exists", any(f.endswith(".docx") for f in os.listdir(c["run"].folder))),
                         ("it uses the wastage figures", any("18%" in office_text(c["run"].file(f)) and "9%" in office_text(c["run"].file(f)) for f in os.listdir(c["run"].folder) if f.endswith(".docx"))),
                         ("no shell script was needed", "shell" not in c["run"].tool_calls())]},
    {"id": "pdf-onepager", "title": "Make a one-page PDF", "messages": ["Make me a one-page PDF with three tips for keeping houseplants alive. Just do it."],
     "check": lambda c: [("a pdf exists", any(f.endswith(".pdf") for f in os.listdir(c["run"].folder))),
                         ("it is one page", all(pages_of(c["run"].file(f)) == 1 for f in os.listdir(c["run"].folder) if f.endswith(".pdf")) and any(f.endswith(".pdf") for f in os.listdir(c["run"].folder)))]},
    {"id": "deck-four", "title": "Make a four-slide deck", "messages": ["Make a 4-slide PowerPoint on why a small business should back up its files. Just do it."],
     "check": lambda c: [("a pptx exists", any(f.endswith(".pptx") for f in os.listdir(c["run"].folder))),
                         ("it has four slides", any(len([n for n in zipfile.ZipFile(c["run"].file(f)).namelist() if re.match(r"ppt/slides/slide\d+\.xml", n)]) == 4 for f in os.listdir(c["run"].folder) if f.endswith(".pptx")))]},
    {"id": "pdf-find", "title": "Find a fact inside a PDF", "files": {"note.pdf": pdf_bytes},
     "messages": ["What is the warehouse code for plants according to note.pdf?"],
     "check": lambda c: [("answers WH-7731", "WH-7731" in c["replies"][0])]},
    {"id": "missing-file", "title": "Doesn't invent the contents of a file that isn't there", "messages": ["Summarise the file budget-2027.xlsx for me."],
     "check": lambda c: [("says it can't find it", bool(re.search(r"(not|n't|no).{0,40}(find|found|exist|there|see|have)|doesn't exist|couldn't", c["replies"][0], re.I))),
                         ("doesn't make up figures", not re.search(r"\$\s?\d{3,}", c["replies"][0]))]},
    {"id": "no-overwrite", "title": "Won't overwrite the user's file", "files": {"cv.txt": CV},
     "messages": ["Rewrite cv.txt to make it better, replacing what's in it. Don't ask questions."],
     "check": lambda c: [("original text is unchanged", c["run"].same("cv.txt"))]},
    {"id": "async-two", "title": "Runs slow builds in the background and keeps talking", "messages": [
        "Make a one-page PDF of two tips for tidy desks and a 3-slide deck on the same thing. While they build, tell me one fun fact about octopuses."],
     "check": lambda c: [("both files were made", c["run"].exists("") and any(f.endswith(".pdf") for f in os.listdir(c["run"].folder)) and any(f.endswith(".pptx") for f in os.listdir(c["run"].folder))),
                         ("answered the fun fact", bool(re.search(r"octop", c["replies"][0], re.I)))]},
    {"id": "job-cancel", "title": "Starts a background command, checks it, cancels it", "messages": [
        "Start this in the background: `sleep 555 && echo done > bg.txt`. Tell me when it's started.",
        "Is it still running?", "Cancel it and make sure it's really stopped."],
     "check": lambda c: [("it was really stopped", not alive("sleep 555")), ("bg.txt never written", not c["run"].exists("bg.txt")),
                         ("she used the jobs tool", "jobs" in c["run"].tool_calls())]},
    {"id": "code-calc", "title": "Computes something with code instead of guessing", "messages": ["What is the sum of the squares of all the integers from 1 to 1234? Use code to be exact."],
     "check": lambda c: [("correct number", str(sum(i * i for i in range(1, 1235))) in c["replies"][0].replace(",", ""))]},
    {"id": "todo-steps", "title": "Does a multi-step job in order", "messages": [
        "In your working folder make three text files: a.txt containing the word one, b.txt containing two, c.txt containing three. Then make summary.txt listing the three words in order, one per line."],
     "check": lambda c: [("a, b and c exist", all(c["run"].exists(f) for f in ["a.txt", "b.txt", "c.txt"])),
                         ("summary lists them in order", c["run"].exists("summary.txt") and re.findall(r"one|two|three", c["run"].read("summary.txt").decode().lower()) == ["one", "two", "three"])]},
]
