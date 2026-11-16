"""
Turns the card art in art/cards (any name, PNG or WebP, square) into the JPGs the mod embeds:
src/CP99Online/Assets/cards/<card id>.jpg, 512 x 512.

Run after adding or changing card art:  python art/convert-cards.py
The file name only has to contain the card's name (spaces, underscores and case don't matter),
e.g. "Double_Dash.webp" -> ultrapuck.doubledash.
"""
import os
import re
import sys
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, 'cards')
OUT = os.path.join(HERE, '..', 'src', 'CP99Online', 'Assets', 'cards')
SIZE = 512

# card name (letters only, lower case) -> card id
CARDS = {
    'doubledash': 'ultrapuck.doubledash',
    'speedup': 'ultrapuck.speedup',
    'powershot': 'ultrapuck.powershot',
    'quickrecharge': 'ultrapuck.quickrecharge',
    'heavyweight': 'ultrapuck.heavyweight',
    'featherweight': 'ultrapuck.featherweight',
    'overcharge': 'ultrapuck.fastercharge',
    'slowpoke': 'ultrapuck.slowpoke',
    'headstart': 'ultrapuck.betterspawn',
    'pylongoalie': 'ultrapuck.pylongoalie',
    'puckmagnet': 'ultrapuck.puckmagnet',
    'puckgun': 'ultrapuck.puckgun',
    'friendlyfire': 'ultrapuck.friendlyfire',
    'invisibility': 'ultrapuck.invisibility',
    'shockwave': 'ultrapuck.shockwave',
    'doubleagent': 'ultrapuck.doubleagent',
    'teamplay': 'ultrapuck.teamplay',
    'teamplayt': 'ultrapuck.teamplay2',  # Team_Play_T2
}


def main():
    os.makedirs(OUT, exist_ok=True)
    done = set()
    for name in sorted(os.listdir(SRC)):
        base, ext = os.path.splitext(name)
        if ext.lower() not in ('.png', '.webp', '.jpg', '.jpeg'):
            continue
        key = re.sub('[^a-z]', '', base.lower())
        card = CARDS.get(key)
        if card is None:
            print('?? ' + name + ': no card called "' + key + '"')
            continue
        im = Image.open(os.path.join(SRC, name)).convert('RGB')
        side = min(im.size)  # centre square, in case it isn't one
        left, top = (im.width - side) // 2, (im.height - side) // 2
        im = im.crop((left, top, left + side, top + side)).resize((SIZE, SIZE), Image.LANCZOS)
        out = os.path.join(OUT, card + '.jpg')
        im.save(out, 'JPEG', quality=90, optimize=True)
        done.add(card)
        print('%-24s -> %s (%d KB)' % (name, os.path.basename(out), os.path.getsize(out) // 1024))
    missing = [c for c in CARDS.values() if c not in done]
    if missing:
        print('no art yet: ' + ', '.join(missing))


if __name__ == '__main__':
    sys.exit(main())
