import argparse, io, logging
from flask import Flask, request, Response, jsonify
from f5_tts.api import F5TTS
import soundfile as sf
import numpy as np

logging.getLogger('werkzeug').setLevel(logging.ERROR)

parser = argparse.ArgumentParser()
parser.add_argument('--model',     required=True)
parser.add_argument('--ref_audio', required=True)
parser.add_argument('--port',      type=int, default=8020)
args = parser.parse_args()

tts = F5TTS(model='F5TTS_v1_Base', ckpt_file=args.model)

app = Flask(__name__)

@app.route('/health')
def health():
    return jsonify({'status': 'ok'})

@app.route('/synthesise', methods=['POST'])
def synthesise():
    text = request.json['text']
    wav, sr, _ = tts.infer(ref_file=args.ref_audio, ref_text='', gen_text=text)
    buf = io.BytesIO()
    sf.write(buf, np.array(wav), sr, format='WAV', subtype='PCM_16')
    return Response(buf.getvalue(), mimetype='audio/wav')

app.run(port=args.port)