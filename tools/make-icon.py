# Draws the package icon (assets/icon.png, 128x128): python tools/make-icon.py assets/icon.png
# Needs Pillow.
import sys
from PIL import Image, ImageDraw

out = sys.argv[1]
S = 4                      # supersampling
N = 128 * S
img = Image.new("RGBA", (N, N), (0, 0, 0, 0))

# Background: rounded square with a vertical gradient (like the Leptonica.Managed icon).
top, bottom = (106, 76, 230), (72, 44, 182)
grad = Image.new("RGBA", (N, N))
gd = ImageDraw.Draw(grad)
for y in range(N):
    t = y / (N - 1)
    c = tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
    gd.line([(0, y), (N, y)], fill=c)
mask = Image.new("L", (N, N), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, N - 1, N - 1], radius=26 * S, fill=255)
img.paste(grad, (0, 0), mask)

d = ImageDraw.Draw(img)
white = (255, 255, 255, 255)
soft = (255, 255, 255, 150)

def p(x, y):
    return (x * S, y * S)

# A syntax tree: root, two inner nodes, five leaves.
root = (64, 30)
inner = [(38, 62), (90, 62)]
leaves = [(22, 96), (44, 96), (66, 96), (88, 96), (108, 96)]
edges = [(root, inner[0]), (root, inner[1]),
         (inner[0], leaves[0]), (inner[0], leaves[1]),
         (inner[1], leaves[2]), (inner[1], leaves[3]), (inner[1], leaves[4])]
for a, b in edges:
    d.line([p(*a), p(*b)], fill=soft, width=5 * S)

def node(c, r, fill):
    x, y = c
    d.ellipse([(x - r) * S, (y - r) * S, (x + r) * S, (y + r) * S], fill=fill)

node(root, 11, white)
for c in inner:
    node(c, 9, white)
for c in leaves:
    # Leaves as small rounded tokens.
    x, y = c
    d.rounded_rectangle([(x - 8) * S, (y - 6) * S, (x + 8) * S, (y + 6) * S], radius=4 * S, fill=white)

img = img.resize((128, 128), Image.LANCZOS)
img.save(out)
