<#
.SYNOPSIS
    The gallery's pages: number, key, sidebar label, and one line about what lives there.

.DESCRIPTION
    One table for every tool that walks the gallery by name — snapshots.ps1 clicks it,
    page-costs.ps1 dwells on it. The numbers are fixed so a README link, or a row in
    docs/PERFORMANCE.md, survives a page being added. Scrolled marks the pages whose nav
    item sits below the sidebar's fold and needs a wheel before it can be clicked. Heading
    is the text the shell draws above the page, which is how a walk proves the click
    landed: the overview has no shell heading, so it names its own hero instead.

    Dot-source it:  . (Join-Path $PSScriptRoot 'gallery-pages.ps1')
#>
$GalleryPages = @(
    [pscustomobject]@{ Number = '01'; Key = 'overview';    Label = 'Overview';             Heading = 'Components for dotnet'; Scrolled = $false }
    [pscustomobject]@{ Number = '02'; Key = 'buttons';     Label = 'Buttons';              Heading = 'Buttons'; Scrolled = $false }
    [pscustomobject]@{ Number = '03'; Key = 'inputs';      Label = 'Inputs';               Heading = 'Inputs'; Scrolled = $false }
    [pscustomobject]@{ Number = '04'; Key = 'selection';   Label = 'Selection';            Heading = 'Selection'; Scrolled = $false }
    [pscustomobject]@{ Number = '05'; Key = 'navigation';  Label = 'Navigation';           Heading = 'Navigation'; Scrolled = $false }
    [pscustomobject]@{ Number = '06'; Key = 'overlays';    Label = 'Overlays';             Heading = 'Overlays'; Scrolled = $false }
    [pscustomobject]@{ Number = '07'; Key = 'feedback';    Label = 'Feedback';             Heading = 'Feedback'; Scrolled = $false }
    [pscustomobject]@{ Number = '08'; Key = 'data';        Label = 'Data display';         Heading = 'Data display'; Scrolled = $false }
    [pscustomobject]@{ Number = '09'; Key = 'charts';      Label = 'Charts';               Heading = 'Charts'; Scrolled = $false }
    [pscustomobject]@{ Number = '10'; Key = 'studio';      Label = 'Studio';               Heading = 'Studio'; Scrolled = $false }
    [pscustomobject]@{ Number = '11'; Key = 'tools';       Label = 'Tools';                Heading = 'Tools'; Scrolled = $false }
    [pscustomobject]@{ Number = '12'; Key = 'devtools';    Label = 'Developer tools';      Heading = 'Developer tools'; Scrolled = $false }
    [pscustomobject]@{ Number = '13'; Key = 'devtools2';   Label = 'More developer tools'; Heading = 'More developer tools'; Scrolled = $false }
    [pscustomobject]@{ Number = '14'; Key = 'actions';     Label = 'Actions';              Heading = 'Actions'; Scrolled = $false }
    [pscustomobject]@{ Number = '15'; Key = 'smartinputs'; Label = 'Smart inputs';         Heading = 'Smart inputs'; Scrolled = $false }
    [pscustomobject]@{ Number = '16'; Key = 'pickers';     Label = 'Pickers';              Heading = 'Pickers'; Scrolled = $true }
    [pscustomobject]@{ Number = '17'; Key = 'capture';     Label = 'Capture';              Heading = 'Capture'; Scrolled = $true }
    [pscustomobject]@{ Number = '18'; Key = 'disclosure';  Label = 'Disclosure';           Heading = 'Disclosure'; Scrolled = $true }
    [pscustomobject]@{ Number = '19'; Key = 'reader';      Label = 'Reader';               Heading = 'Reader'; Scrolled = $true }
    [pscustomobject]@{ Number = '20'; Key = 'datagrid';    Label = 'Data grid';            Heading = 'Data grid'; Scrolled = $true }
    [pscustomobject]@{ Number = '21'; Key = 'code';        Label = 'Code';                 Heading = 'Code'; Scrolled = $true }
    [pscustomobject]@{ Number = '22'; Key = 'social';      Label = 'Social and media';     Heading = 'Social and media'; Scrolled = $true }
    [pscustomobject]@{ Number = '23'; Key = 'planning';    Label = 'Planning';             Heading = 'Planning'; Scrolled = $true }
    [pscustomobject]@{ Number = '24'; Key = 'morecharts';  Label = 'More charts';          Heading = 'More charts'; Scrolled = $true }
    [pscustomobject]@{ Number = '25'; Key = 'motion';      Label = 'Motion';               Heading = 'Motion'; Scrolled = $true }
    [pscustomobject]@{ Number = '26'; Key = 'ai';          Label = 'AI';                   Heading = 'AI'; Scrolled = $true }
    [pscustomobject]@{ Number = '27'; Key = 'audio';       Label = 'Audio';                Heading = 'Audio'; Scrolled = $true }
    [pscustomobject]@{ Number = '28'; Key = 'files';       Label = 'Files';                Heading = 'Files'; Scrolled = $true }
    [pscustomobject]@{ Number = '29'; Key = 'instruments'; Label = 'Instruments';          Heading = 'Instruments';        Scrolled = $true }
)
