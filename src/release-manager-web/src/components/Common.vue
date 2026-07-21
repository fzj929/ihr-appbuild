<script lang="ts">
import { defineComponent, h } from 'vue'
export const PageHeader = defineComponent({ props:{ eyebrow:String,title:String,description:String }, setup(p,{slots}){return()=>h('header',{class:'page-head'},[h('div',{},[h('span',{class:'eyebrow'},p.eyebrow),h('h1',{},p.title),p.description?h('p',{},p.description):null]),h('div',{class:'head-actions'},slots.default?.())])} })
export const StatusBadge = defineComponent({ props:{value:{type:String,required:true}}, setup(p){ const ok=['Succeeded','Running','Ready'].includes(p.value), bad=['Failed','Crashed','Interrupted'].includes(p.value); return()=>h('span',{class:['status',ok?'ok':bad?'bad':'']},[h('i'),p.value]) } })
export default defineComponent({ name: 'CommonComponents' })
</script>
