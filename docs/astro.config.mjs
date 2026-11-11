// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// Published to GitHub Pages at https://lv3rmeulen.github.io/Deployify/ by .github/workflows/docs.yml.
// With a custom domain, set `site` to it and remove `base`.
export default defineConfig({
	site: 'https://lv3rmeulen.github.io',
	base: '/Deployify',
	// Pages import code straight from ../samples, so the dev server may read outside docs/.
	vite: { server: { fs: { allow: ['..'] } } },
	integrations: [
		starlight({
			title: 'Deployify',
			description: 'Deploy .NET Aspire apps to Nomad, Kubernetes, Docker Compose, AWS ECS, Azure Container Apps and more.',
			logo: { src: './src/assets/deployify-icon.png' },
			favicon: '/favicon.png',
			social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/lv3rmeulen/Deployify' }],
			editLink: { baseUrl: 'https://github.com/lv3rmeulen/Deployify/edit/main/docs/' },
			lastUpdated: true,
			sidebar: [
				{
					label: 'Start here',
					items: [
						'getting-started/installation',
						'getting-started/quick-start',
						'getting-started/first-deployment',
						'samples',
					],
				},
				{
					label: 'Guides',
					items: [
						'guides/deploying',
						'guides/rollouts',
						'guides/prune-and-destroy',
						'guides/parameters-and-secrets',
						'guides/publishing-images',
						'guides/ci-cd',
						'guides/aspire-deploy',
						'guides/dapr',
						'guides/dashboard',
						'guides/nomad-raw-exec',
					],
				},
				{
					label: 'Deployment targets',
					items: [
						'targets/compose',
						'targets/kubernetes',
						'targets/helm',
						'targets/gitops',
						'targets/ovhcloud',
						'targets/nomad',
						'targets/nomad-consul',
						'targets/aws-ecs',
						'targets/azure-container-apps',
						'targets/plugins',
					],
				},
				{ label: 'Concepts', items: ['concepts/state'] },
				{ label: 'Extending', items: ['extending/plugins'] },
				{
					label: 'Reference',
					items: [
						'reference/state-file',
						{ label: 'CLI', collapsed: true, items: [{ autogenerate: { directory: 'reference/cli' } }] },
					],
				},
				{
					label: 'Contributing',
					collapsed: true,
					items: ['contributing/project-structure', 'contributing/adding-a-target', 'contributing/docs'],
				},
			],
		}),
	],
});
