import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';

const ROOT = path.resolve(process.cwd());
const INDEX = path.join(ROOT, 'index.html');
const OUT_DIR = path.join(ROOT, 'audio', 'rap');
const MANIFEST_PATH = path.join(OUT_DIR, 'manifest.json');

const API_KEY = process.env.OPENAI_API_KEY || '';
const MODEL = process.env.RAP_MODEL || 'gpt-4o-mini-tts-2025-12-15';
const VOICE = process.env.RAP_VOICE || 'marin';
const MAX_NEW = Math.max(0, Number(process.env.RAP_MAX_NEW || 0));
const FORCE = String(process.env.RAP_FORCE || '').toLowerCase() === 'true';

const INSTRUCTIONS = [
  'Speak in natural Japanese like a real adult tutor helping someone memorize law.',
  'Deliver the line as one continuous human voice with a catchy spoken 4/4 groove.',
  'Do not sing a melody and do not sound like a metronome or robot.',
  'Use subtle rhythmic bounce, natural breath, and conversational timing.',
  'Keep pitch changes small and human. Give a little extra emphasis to the final key word.',
  'Pronounce Japanese legal terms precisely and clearly.',
  'Do not add words, counts, commentary, or sound effects that are not in the input.'
].join(' ');

function extractScripts(html) {
  return [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(m => m[1]);
}

function parseData(dataScript) {
  const marker = 'window.GYOSEI_DATA=';
  const start = dataScript.indexOf(marker);
  if (start < 0) throw new Error('window.GYOSEI_DATA not found');
  let json = dataScript.slice(start + marker.length).trim();
  if (json.endsWith(';')) json = json.slice(0, -1);
  return JSON.parse(json);
}

function normalizeData(DATA) {
  const mockQuestions = [];
  Object.entries(DATA.mocks || {}).forEach(([mock, qs]) => (qs || []).forEach(q => {
    mockQuestions.push({ ...q, type: 'mock', mock, topic: q.field || q.subject });
  }));
  const predicted = (DATA.predicted30 || []).map(q => ({
    ...q, type: 'predicted', format: '5肢択一', section: '法令等',
    topic: q.topic || q.field, points: 4, mock: ''
  }));
  const propositions = (DATA.propositions || [])
    .filter(p => p.correct !== null)
    .map(p => ({ ...p, type: 'proposition', format: '○×', topic: p.tag || p.field, points: 1 }));
  const mnemonics = DATA.mnemonics || [];
  return { mockQuestions, predicted, propositions, mnemonics };
}

function buildPhraseFunctions(appScript, vars) {
  const start = appScript.indexOf('const RAP_RULES = [');
  const end = appScript.indexOf('const TTS_READINGS = [', start);
  if (start < 0 || end < 0) throw new Error('rap phrase block not found');
  const code = appScript.slice(start, end);
  return new Function('mnemonics', 'propositions', 'mockQuestions', 'predicted',
    code + '\nreturn { rapPhraseFor };'
  )(vars.mnemonics, vars.propositions, vars.mockQuestions, vars.predicted);
}

function buildSpeechFunctions(appScript) {
  const start = appScript.indexOf('const TTS_READINGS = [');
  const end = appScript.indexOf('function splitRapBeats(text){', start);
  if (start < 0 || end < 0) throw new Error('TTS reading block not found');
  const code = appScript.slice(start, end);
  return new Function(code + '\nreturn { speechTextForRap };')();
}

function phraseId(phrase) {
  return crypto.createHash('sha256').update(phrase, 'utf8').digest('hex').slice(0, 24);
}

function sleep(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

async function requestSpeech(input) {
  let lastError = null;
  for (let attempt = 0; attempt < 6; attempt++) {
    try {
      const response = await fetch('https://api.openai.com/v1/audio/speech', {
        method: 'POST',
        headers: {
          'Authorization': 'Bearer ' + API_KEY,
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({
          model: MODEL,
          voice: VOICE,
          input,
          instructions: INSTRUCTIONS,
          response_format: 'mp3',
          speed: 1.0
        })
      });

      if (response.ok) return Buffer.from(await response.arrayBuffer());

      const detail = await response.text();
      const error = new Error('OpenAI speech API ' + response.status + ': ' + detail.slice(0, 500));
      if (response.status !== 429 && response.status < 500) throw error;
      lastError = error;
    } catch (error) {
      lastError = error;
    }

    if (attempt < 5) await sleep(Math.min(30000, 1500 * (2 ** attempt)));
  }
  throw lastError || new Error('speech generation failed');
}

async function main() {
  if (!API_KEY) throw new Error('OPENAI_API_KEY is not set');

  const html = await fs.readFile(INDEX, 'utf8');
  const scripts = extractScripts(html);
  const dataScript = scripts.find(s => s.includes('window.GYOSEI_DATA='));
  const appScript = scripts.find(s => s.includes("const APP_VERSION ="));
  if (!dataScript || !appScript) throw new Error('required script blocks not found');

  const DATA = parseData(dataScript);
  const vars = normalizeData(DATA);
  const { rapPhraseFor } = buildPhraseFunctions(appScript, vars);
  const { speechTextForRap } = buildSpeechFunctions(appScript);

  const allItems = [...vars.mockQuestions, ...vars.predicted, ...vars.propositions];
  const phrases = [...new Set(
    allItems.map(it => rapPhraseFor(it, '', it.explanation || '')).filter(Boolean)
  )].sort((a, b) => a.localeCompare(b, 'ja'));

  await fs.mkdir(OUT_DIR, { recursive: true });

  const items = {};
  let generated = 0;
  let reused = 0;
  let deferred = 0;

  for (let i = 0; i < phrases.length; i++) {
    const phrase = phrases[i];
    const id = phraseId(phrase);
    const filename = id + '.mp3';
    const filePath = path.join(OUT_DIR, filename);
    const publicPath = './audio/rap/' + filename;

    let exists = false;
    try {
      const stat = await fs.stat(filePath);
      exists = stat.isFile() && stat.size > 1000;
    } catch {}

    if (exists && !FORCE) {
      items[phrase] = publicPath;
      reused++;
      continue;
    }

    if (MAX_NEW > 0 && generated >= MAX_NEW) {
      if (exists) items[phrase] = publicPath;
      deferred++;
      continue;
    }

    const spoken = speechTextForRap(phrase);
    process.stdout.write('[' + (i + 1) + '/' + phrases.length + '] ' + phrase + ' ... ');
    const audio = await requestSpeech(spoken);
    await fs.writeFile(filePath, audio);
    items[phrase] = publicPath;
    generated++;
    console.log('ok (' + audio.length + ' bytes)');

    // Stay comfortably below common entry-tier request limits.
    await sleep(160);
  }

  const manifest = {
    version: 1,
    generatedAt: new Date().toISOString(),
    model: MODEL,
    voice: VOICE,
    disclosure: 'AI-generated voice',
    phraseCount: phrases.length,
    audioCount: Object.keys(items).length,
    items
  };
  await fs.writeFile(MANIFEST_PATH, JSON.stringify(manifest, null, 2) + '\n', 'utf8');

  console.log(JSON.stringify({
    phraseCount: phrases.length,
    generated,
    reused,
    deferred,
    manifestItems: Object.keys(items).length,
    model: MODEL,
    voice: VOICE
  }, null, 2));
}

main().catch(error => {
  console.error(error);
  process.exit(1);
});
