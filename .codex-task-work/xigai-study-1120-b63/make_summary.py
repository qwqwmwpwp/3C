from pathlib import Path
import json, re
from reportlab.pdfgen import canvas
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.lib import colors
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.lib.styles import ParagraphStyle
from reportlab.platypus import Paragraph
from xml.sax.saxutils import escape

base = Path(__file__).parent
payload = json.loads((base / 'topics.json').read_text(encoding='utf-8-sig'))
chapters = payload['chapters']
fontdir = Path('C:/Windows/Fonts')
pdfmetrics.registerFont(TTFont('YaHei', str(fontdir / 'msyh.ttc'), subfontIndex=0))
pdfmetrics.registerFont(TTFont('YaHeiBold', str(fontdir / 'msyhbd.ttc'), subfontIndex=0))
pdf = canvas.Canvas(str(base / '习概高频考点按教材章节汇总.pdf'), pagesize=A4)
pdf.setTitle('习近平新时代中国特色社会主义思想概论 高频考点按教材章节汇总')
pdf.setAuthor('课程复习资料整理')
W,H=A4
left=43
right=W-43
body=colors.HexColor('#202831')
muted=colors.HexColor('#475766')
teal=colors.HexColor('#14515A')
line=colors.HexColor('#CBD5D6')
chapter_number = ['导论'] + ['第'+n+'章' for n in ['一','二','三','四','五','六','七','八','九','十','十一','十二','十三','十四','十五','十六','十七']]
assert [ch[0] for ch in chapters] == chapter_number

def put(x,y,t,size=10,font='YaHei',color=body):
    pdf.setFillColor(color)
    pdf.setFont(font,size)
    pdf.drawString(x,y,t)

def paragraph(t,y,size=10.4,leading=16.0,x=left,limit=None,color=body):
    sty=ParagraphStyle('p',fontName='YaHei',fontSize=size,leading=leading,textColor=color,wordWrap='CJK',splitLongWords=1)
    p=Paragraph(escape(t),sty)
    width=(right-x) if limit is None else limit
    _,h=p.wrap(width,2000)
    p.drawOn(pdf,x,y-h)
    return y-h

def reference(s):
    out=[]
    for v in s.split():
        k,n=v[0],v[1:]
        if n.startswith('#'): out.append(k+' 第'+n[1:]+'题')
        elif n.startswith('单') or n.startswith('多'): out.append(k+' '+n[0]+'选'+n[1:])
        else: out.append(k+' 第'+n+'页')
    return '  ·  '.join(out)

def chapter(ch,y,idx):
    label,title,items=ch
    pdf.bookmarkHorizontalAbsolute('ch'+str(idx),y+3)
    pdf.addOutlineEntry(label+'  '+title,'ch'+str(idx),level=0,closed=False)
    put(left,y-14,label+'  '+title,14,'YaHeiBold',teal)
    y-=25
    for title,refs,detail in items:
        letters=[v[0] for v in refs.split()]
        assert len(set(letters)) == len(letters)
        num=len(letters)
        put(left,y-10,title,11.3,'YaHeiBold')
        pdf.setFont('YaHeiBold',10.2)
        pdf.setFillColor(teal if num>=5 else muted)
        pdf.drawRightString(right,y-10,f'{num}/6 份')
        y-=16
        y=paragraph(detail,y)
        y-=3
        y=paragraph('原题  '+reference(refs),y,8.6,12.5,color=muted)
        y-=10
    return y-4

def frame(pg,scope):
    put(left,H-31,'习近平新时代中国特色社会主义思想概论',9,'YaHei',muted)
    pdf.setFillColor(muted);pdf.setFont('YaHei',9)
    pdf.drawRightString(right,H-31,scope)
    pdf.setStrokeColor(line);pdf.line(left,H-40,right,H-40)
    pdf.line(left,34,right,34)
    put(left,19,'资料覆盖数按文件去重。字母与原文件的对应关系见末页。',8,'YaHei',muted)
    pdf.setFillColor(muted);pdf.setFont('YaHei',8.5);pdf.drawRightString(right,19,f'{pg} / 6')
    return H-59

def note(title,t,y):
    put(left,y-12,title,11,'YaHeiBold'); y-=19
    return paragraph(t,y,10,15)-9

groups=[[0,1],[2,3,4],[5,6,7,8],[9,10,11,12],[13,14,15],[16,17]]
scope=['导论至第一章','第二至第四章','第五至第八章','第九至第十二章','第十三至第十五章','第十六章至结语及资料索引']
for pg,group in enumerate(groups,1):
    y=frame(pg,scope[pg-1])
    if pg==1:
        put(left,y-19,'高频考点按教材章节汇总',23,'YaHeiBold')
        y-=33
        y=paragraph('按2023年版统编教材的导论、十七章和结语排列。共核对6份复习资料，归并出41个考点或相关题组。每项列出代表性原题位置，可据此回到原文自测。',y,10.4,16)-6
        y=paragraph('计数方法：一份资料的题干或提纲标题直接考查该项，记1份。同一文件的重复页、重复题、多种题型只计一次。仅在答案中顺带提及的内容不计。资料之间有重合，覆盖数只能说明这批资料的复习重点。',y,10.0,15)-8
        y=paragraph('原题索引：A 扫描提纲，B 综合题库，C 大题汇编，D 22题，E 逆序提纲，F 26题。页码均指原资料页。',y,9.2,14,color=muted)-14
    for idx in group:
        y=chapter(chapters[idx],y,idx)
    if pg==2:
        y=note('补全第3题的答案','D 第3题同时问“中国特色”和“本质要求”，其答案只列出第一项具体特色。复习时先默写五项中国特色，再用九项本质要求自测。',y)
    if pg==5:
        y=note('同一考点怎样换题型','强军目标既能出成“作风优良”填空式单选，也能出成三项全选的多选，还能要求逐项解释。先闭卷写出三句，再把每句话与其作用一一配对。',y)
    if pg==6:
        pdf.bookmarkHorizontalAbsolute('end',y)
        pdf.addOutlineEntry('结语与资料索引','end',level=0,closed=False)
        y=note('结语','这6份材料未单列以教材结语为题的习题，未计算独立考点。',y)
        put(left,y-12,'资料索引',13,'YaHeiBold',teal);y-=22
        for key,name,desc in payload['files']:
            put(left,y-9,key,9.6,'YaHeiBold',teal)
            put(left+19,y-9,name,9.4)
            pdf.setFont('YaHei',9);pdf.setFillColor(muted);pdf.drawRightString(right,y-9,desc)
            y-=18
        y-=6
        y=paragraph('章节依据：高等教育出版社、人民出版社《习近平新时代中国特色社会主义思想概论》（2023年版，ISBN 978-7-04-061053-6）。中国式现代化本质要求参照党的二十大报告核对。',y,9,14,color=muted)-7
        for label,url in [('高等教育出版社教材目录','https://xuanshu.hep.com.cn/front/h5Mobile/bookDetails?bookId=64d518c9938b7cc2960f0bbe'),('党的二十大报告全文','https://app.www.gov.cn/govdata/gov/202210/25/493662/article.html')]:
            put(left,y-8,label,8.8,'YaHei',teal)
            pdf.linkURL(url,(left,y-10,left+pdfmetrics.stringWidth(label,'YaHei',8.8),y+1),relative=0)
            y-=15
    print(pg, round(y,1))
    if y<38: raise ValueError(f'Page {pg} overflows by {38-y:.1f}pt')
    pdf.showPage()
pdf.save()

