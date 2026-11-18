import { DatabaseSync } from 'node:sqlite';
import { readFileSync, readdirSync } from 'node:fs';

// Real SQLite executes every SQL statement, including transactional races/CAS.
// This adapter is for tests only; Worker production code only uses D1 bindings.
export function database() {
  const sqlite = new DatabaseSync(':memory:');
  const migrations = new URL('../migrations/', import.meta.url);
  for (const name of readdirSync(migrations).filter(name => name.endsWith('.sql')).sort()) {
    sqlite.exec(readFileSync(new URL(name, migrations), 'utf8'));
  }
  class Statement {
    constructor(sql,bindings=[]) {this.sql=sql;this.bindings=bindings;}
    bind(...bindings) {return new Statement(this.sql,bindings);}
    async first() {return sqlite.prepare(this.sql).get(...this.bindings) ?? null;}
    async all() {return {results:sqlite.prepare(this.sql).all(...this.bindings)};}
    async run() {const result=sqlite.prepare(this.sql).run(...this.bindings);return {meta:{changes:Number(result.changes)}};}
  }
  return {
    sqlite,
    prepare(sql){return new Statement(sql);},
    async batch(statements) {
      sqlite.exec('BEGIN IMMEDIATE');
      try {
        const results=[];
        for(const s of statements) {
          const prepared=sqlite.prepare(s.sql);
          if(/RETURNING\s/i.test(s.sql)) {const rows=prepared.all(...s.bindings);results.push({results:rows,meta:{changes:rows.length}});}
          else if(/^(SELECT|WITH)/i.test(s.sql.trim())) results.push({results:prepared.all(...s.bindings),meta:{changes:0}});
          else {const result=prepared.run(...s.bindings);results.push({results:[],meta:{changes:Number(result.changes)}});}
        }
        sqlite.exec('COMMIT'); return results;
      }catch(e){sqlite.exec('ROLLBACK');throw e;}
    }
  };
}
