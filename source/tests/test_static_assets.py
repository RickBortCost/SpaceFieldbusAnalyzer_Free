"""Controlli statici sull'interfaccia: sintassi JS, riferimenti del template, parità del catalogo i18n, id duplicati."""
import json
import re
import shutil
import subprocess
import unittest
from collections import Counter
from pathlib import Path

SOURCE = Path(__file__).resolve().parents[1]
STATIC = SOURCE / 'static'
TEMPLATE = SOURCE / 'templates' / 'index.html'
# Pages of the start choice and of the Live Acquisition API mode (they do not load the SFA interface).
LIGHT_PAGES = [SOURCE / 'templates' / 'mode.html', SOURCE / 'templates' / 'live_api.html']


class StaticAssetsTests(unittest.TestCase):
    def test_javascript_syntax(self):
        node = shutil.which('node')
        if not node:
            self.skipTest('node non disponibile')
        for script in sorted(STATIC.glob('*.js')):
            result = subprocess.run([node, '--check', str(script)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, f'{script.name}: {result.stderr}')

    def test_template_references_exist(self):
        html = TEMPLATE.read_text(encoding='utf-8')
        refs = re.findall(r'(?:src|href)="/static/([^"?#]+)', html)
        self.assertTrue(refs)
        for ref in refs:
            self.assertTrue((STATIC / ref).is_file(), f'riferimento mancante nel template: {ref}')

    def test_template_ids_are_unique(self):
        html = TEMPLATE.read_text(encoding='utf-8')
        ids = re.findall(r'\sid="([^"]+)"', html)
        duplicates = [name for name, count in Counter(ids).items() if count > 1]
        self.assertEqual(duplicates, [], f'id duplicati: {duplicates}')

    def test_i18n_catalog_has_same_keys_in_every_language(self):
        text = (STATIC / 'i18n-catalog.js').read_text(encoding='utf-8')
        payload = text.split('window.SPACE_ANALYZER_MESSAGES=', 1)[1].strip().rstrip(';')
        catalog = json.loads(payload)
        self.assertEqual(set(catalog), {'it', 'en', 'de', 'fr', 'es'})
        reference = set(catalog['it'])
        for language, messages in catalog.items():
            missing = reference - set(messages)
            self.assertEqual(missing, set(), f'{language}: chiavi mancanti {sorted(missing)[:5]}')


    def test_light_pages(self):
        catalog = json.loads(re.search(r'window\.SPACE_ANALYZER_MESSAGES=(\{.*\});', (STATIC / 'i18n-catalog.js').read_text(encoding='utf-8')).group(1))
        for page in LIGHT_PAGES:
            html = page.read_text(encoding='utf-8')
            for ref in re.findall(r'(?:src|href)="/static/([^"?#]+)', html):
                self.assertTrue((STATIC / ref).is_file(), f'{page.name}: riferimento mancante {ref}')
            self.assertNotIn('/static/app.js', html, f'{page.name} non deve caricare l\'interfaccia di SFA')
            self.assertIn('name="opcua-token"', html)
            ids = re.findall(r'\sid="([^"]+)"', html)
            self.assertEqual([n for n, c in Counter(ids).items() if c > 1], [], page.name)
            for key in re.findall(r'data-i18n(?:-[a-z-]+)?="([^"]+)"', html):
                self.assertIn(key, catalog['it'], f'{page.name}: testo senza traduzione: {key}')
        for script in ('mode.js', 'live-api.js'):
            for key in re.findall(r"\bt\('([^']+)'\)", (STATIC / script).read_text(encoding='utf-8')):
                self.assertIn(key, catalog['it'], f'{script}: testo senza traduzione: {key}')


if __name__ == '__main__':
    unittest.main()
